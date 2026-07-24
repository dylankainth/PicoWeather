using UnityEngine;
using WeatherVR.Data;

namespace WeatherVR.Weather
{
    /// <summary>
    /// The five weather looks the carousel can switch between. They line up 1:1 with
    /// <c>WeatherCarouselIcon</c>, but this enum lives in the weather module so the
    /// renderers never have to reference the UI.
    /// </summary>
    public enum WeatherSceneKind
    {
        Clear,
        PartlyCloudy,
        Cloudy,
        Rain,
        Storm
    }

    /// <summary>
    /// Everything a single weather scene needs, split into two halves:
    ///
    ///   • physical field targets used to synthesise a <see cref="WeatherDataset"/>
    ///     that the existing cloud/rain/lightning renderers already know how to draw,
    ///   • a glass-themed environment palette (sky gradient, sun, fog, floor tint) so
    ///     the surround matches the carousel UI rather than sitting in black.
    ///
    /// Realistic-but-glass: the physical half keeps the effects believable, the
    /// palette half grades the whole scene to the frosted-glass look of the UI.
    /// </summary>
    public struct WeatherSceneProfile
    {
        public WeatherSceneKind Kind;

        // ---- physical field targets ------------------------------------------
        public float CloudCover;   // 0..1 mean total cover
        public float PrecipMmHr;   // mean precipitation rate
        public float CapeJkg;      // instability, drives the lightning proxy
        public float WindMs;       // mean wind magnitude
        public float TempC;        // mean 2 m temperature

        // ---- glass environment palette ---------------------------------------
        public Color SkyZenith, SkyHorizon, SkyNadir;
        public Color SunGlow;   public float SunGlowStrength;
        public Color SunColor;  public float SunIntensity;
        public float SunElevation, SunAzimuth;
        public Color Ambient;
        public Color FogColor;  public float FogDensity;
        public Color GlassInner, GlassOuter; // frosted floor tint

        /// <summary>
        /// Nudges the palette toward the selected card's accent and glass tint, so
        /// the terrain surround visibly agrees with the card the user just tapped.
        /// Physical fields are untouched — colour only.
        /// </summary>
        public WeatherSceneProfile TintWith(Color accent, Color glassTint)
        {
            WeatherSceneProfile p = this;
            p.SkyHorizon = Color.Lerp(p.SkyHorizon, accent, 0.22f);
            p.SunGlow = Color.Lerp(p.SunGlow, accent, 0.35f);
            p.GlassInner = Color.Lerp(p.GlassInner, WithAlpha(glassTint, p.GlassInner.a), 0.5f);
            p.GlassInner = Color.Lerp(p.GlassInner, WithAlpha(accent, p.GlassInner.a), 0.15f);
            return p;
        }

        static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
    }

    /// <summary>
    /// Profile table plus the dataset synthesiser. Kept static and Unity-runtime-only
    /// (no editor dependency) so scene switching works in a player build.
    /// </summary>
    public static class WeatherScene
    {
        public static WeatherSceneProfile Default(WeatherSceneKind kind)
        {
            switch (kind)
            {
                case WeatherSceneKind.Clear:
                    return new WeatherSceneProfile
                    {
                        Kind = kind,
                        CloudCover = 0.06f, PrecipMmHr = 0f, CapeJkg = 250f, WindMs = 2.5f, TempC = 23f,
                        SkyZenith = new Color(0.11f, 0.24f, 0.47f),
                        SkyHorizon = new Color(0.46f, 0.58f, 0.72f),
                        SkyNadir = new Color(0.07f, 0.10f, 0.16f),
                        SunGlow = new Color(1.00f, 0.78f, 0.50f), SunGlowStrength = 1.7f,
                        SunColor = new Color(1.00f, 0.96f, 0.88f), SunIntensity = 1.45f,
                        SunElevation = 55f, SunAzimuth = 145f,
                        Ambient = new Color(0.38f, 0.42f, 0.49f),
                        FogColor = new Color(0.30f, 0.40f, 0.52f), FogDensity = 0.03f,
                        GlassInner = new Color(0.40f, 0.62f, 0.90f, 0.45f),
                        GlassOuter = new Color(0.08f, 0.16f, 0.32f, 0.0f)
                    };

                case WeatherSceneKind.PartlyCloudy:
                    return new WeatherSceneProfile
                    {
                        Kind = kind,
                        CloudCover = 0.38f, PrecipMmHr = 0f, CapeJkg = 500f, WindMs = 3.5f, TempC = 20f,
                        SkyZenith = new Color(0.12f, 0.22f, 0.40f),
                        SkyHorizon = new Color(0.48f, 0.56f, 0.66f),
                        SkyNadir = new Color(0.07f, 0.09f, 0.14f),
                        SunGlow = new Color(0.95f, 0.82f, 0.62f), SunGlowStrength = 1.3f,
                        SunColor = new Color(0.98f, 0.95f, 0.90f), SunIntensity = 1.20f,
                        SunElevation = 48f, SunAzimuth = 150f,
                        Ambient = new Color(0.36f, 0.40f, 0.46f),
                        FogColor = new Color(0.30f, 0.38f, 0.48f), FogDensity = 0.04f,
                        GlassInner = new Color(0.36f, 0.54f, 0.80f, 0.45f),
                        GlassOuter = new Color(0.07f, 0.13f, 0.26f, 0.0f)
                    };

                case WeatherSceneKind.Cloudy:
                    return new WeatherSceneProfile
                    {
                        Kind = kind,
                        CloudCover = 0.86f, PrecipMmHr = 0.1f, CapeJkg = 350f, WindMs = 4.5f, TempC = 16f,
                        SkyZenith = new Color(0.20f, 0.24f, 0.30f),
                        SkyHorizon = new Color(0.44f, 0.48f, 0.54f),
                        SkyNadir = new Color(0.09f, 0.10f, 0.13f),
                        SunGlow = new Color(0.70f, 0.72f, 0.76f), SunGlowStrength = 0.8f,
                        SunColor = new Color(0.86f, 0.88f, 0.92f), SunIntensity = 0.85f,
                        SunElevation = 42f, SunAzimuth = 155f,
                        Ambient = new Color(0.34f, 0.36f, 0.40f),
                        FogColor = new Color(0.32f, 0.35f, 0.40f), FogDensity = 0.06f,
                        GlassInner = new Color(0.42f, 0.50f, 0.60f, 0.42f),
                        GlassOuter = new Color(0.10f, 0.12f, 0.16f, 0.0f)
                    };

                case WeatherSceneKind.Rain:
                    return new WeatherSceneProfile
                    {
                        Kind = kind,
                        CloudCover = 0.92f, PrecipMmHr = 4.0f, CapeJkg = 600f, WindMs = 6f, TempC = 13f,
                        SkyZenith = new Color(0.13f, 0.17f, 0.24f),
                        SkyHorizon = new Color(0.30f, 0.37f, 0.46f),
                        SkyNadir = new Color(0.06f, 0.08f, 0.11f),
                        SunGlow = new Color(0.52f, 0.60f, 0.72f), SunGlowStrength = 0.6f,
                        SunColor = new Color(0.72f, 0.78f, 0.88f), SunIntensity = 0.6f,
                        SunElevation = 34f, SunAzimuth = 160f,
                        Ambient = new Color(0.28f, 0.32f, 0.38f),
                        FogColor = new Color(0.24f, 0.30f, 0.38f), FogDensity = 0.10f,
                        GlassInner = new Color(0.30f, 0.46f, 0.66f, 0.46f),
                        GlassOuter = new Color(0.05f, 0.10f, 0.18f, 0.0f)
                    };

                default: // Storm
                    return new WeatherSceneProfile
                    {
                        Kind = WeatherSceneKind.Storm,
                        CloudCover = 0.97f, PrecipMmHr = 12f, CapeJkg = 2600f, WindMs = 9f, TempC = 18f,
                        SkyZenith = new Color(0.09f, 0.10f, 0.17f),
                        SkyHorizon = new Color(0.24f, 0.24f, 0.36f),
                        SkyNadir = new Color(0.05f, 0.05f, 0.09f),
                        SunGlow = new Color(0.46f, 0.44f, 0.70f), SunGlowStrength = 0.7f,
                        SunColor = new Color(0.60f, 0.64f, 0.82f), SunIntensity = 0.45f,
                        SunElevation = 26f, SunAzimuth = 165f,
                        Ambient = new Color(0.22f, 0.24f, 0.34f),
                        FogColor = new Color(0.16f, 0.18f, 0.28f), FogDensity = 0.14f,
                        GlassInner = new Color(0.34f, 0.36f, 0.62f, 0.48f),
                        GlassOuter = new Color(0.05f, 0.06f, 0.13f, 0.0f)
                    };
            }
        }

        /// <summary>
        /// Builds a <see cref="WeatherDataset"/> that draws as the given profile. The
        /// storm reuses the existing squall-line generator (its structure is what
        /// makes lightning cluster and rain streak convincingly); the calmer scenes
        /// fill a grid from the profile with mild noise so they are not dead flat.
        /// </summary>
        public static WeatherDataset Synthesize(WeatherSceneProfile p, GeoBounds bounds, int gridSize, int seed)
        {
            if (p.Kind == WeatherSceneKind.Storm)
                return ProceduralWeather.Generate(bounds, gridSize, seed);

            gridSize = Mathf.Clamp(gridSize, 4, 64);

            WeatherDataset dataset = NewSceneDataset(bounds, gridSize, p);

            for (int y = 0; y < gridSize; y++)
            {
                float v = y / (float)(gridSize - 1);
                for (int x = 0; x < gridSize; x++)
                {
                    float u = x / (float)(gridSize - 1);
                    dataset.cells[y * gridSize + x] = CellFor(u, v, p, seed);
                }
            }

            return dataset;
        }

        static WeatherCell CellFor(float u, float v, WeatherSceneProfile p, int seed)
        {
            // 0..1 lumpiness so a broken sky reads as broken, not as a uniform sheet.
            float lump = Noise.Fbm2(u * 4.2f, v * 4.2f, 3, 2f, 0.55f, seed + 11);
            float detail = Noise.Fbm2(u * 9f, v * 9f, 3, 2f, 0.5f, seed + 29);

            float cover = Mathf.Clamp01(p.CloudCover + (lump - 0.5f) * 0.32f);
            float low = Mathf.Clamp01(cover);
            float mid = Mathf.Clamp01(cover * 0.82f + (detail - 0.5f) * 0.15f);
            float high = Mathf.Clamp01(cover * 0.5f + (lump - 0.5f) * 0.2f);
            float total = 1f - (1f - low) * (1f - mid) * (1f - high);

            float precip = Mathf.Max(0f, p.PrecipMmHr * (0.55f + 0.9f * lump) - 0.1f);
            float cape = p.CapeJkg * (0.7f + 0.6f * detail);
            float temperature = p.TempC + (lump - 0.5f) * 2.5f;

            return new WeatherCell
            {
                cloudTotal = total,
                cloudLow = low,
                cloudMid = mid,
                cloudHigh = high,
                precipitationMmHr = precip,
                capeJkg = cape,
                lightningPotential = Atmosphere.LightningPotential(cape, precip),
                temperatureC = temperature,
                windU = p.WindMs * 0.8f,
                windV = p.WindMs * 0.4f
            };
        }

        /// <summary>
        /// A <see cref="WeatherDataset"/> pre-filled with header/provenance for a
        /// synthesised scene, cells still empty. Written as a helper so
        /// <see cref="Synthesize"/> stays readable.
        /// </summary>
        static WeatherDataset NewSceneDataset(GeoBounds bounds, int gridSize, WeatherSceneProfile p)
        {
            return new WeatherDataset
            {
                schemaVersion = WeatherDataset.CurrentSchemaVersion,
                source = "procedural",
                attribution = $"Synthetic \"{p.Kind}\" scene — not observed data.",
                observationTimeUtc = "",
                generatedUtc = "",
                minLatitude = bounds.MinLatitude,
                maxLatitude = bounds.MaxLatitude,
                minLongitude = bounds.MinLongitude,
                maxLongitude = bounds.MaxLongitude,
                gridWidth = gridSize,
                gridHeight = gridSize,
                layers = WeatherDataset.DefaultLayers(),
                cells = new WeatherCell[gridSize * gridSize]
            };
        }
    }
}

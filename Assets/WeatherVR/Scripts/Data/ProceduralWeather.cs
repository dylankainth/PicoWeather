using System;
using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// Fallback weather for when neither the live fetch nor a baked
    /// <c>weather.json</c> is available.
    ///
    /// Rather than noise-for-its-own-sake this lays out the structure of a real
    /// summer mesoscale convective system over the delta, because that is what makes
    /// the volumetric render legible: a squall line oriented NNE–SSW with a narrow
    /// convective core, a broad trailing stratiform shield behind it, an anvil of
    /// cirrus blown downshear ahead of it, and CAPE peaking just ahead of the line
    /// where the atmosphere has not yet been overturned.
    ///
    /// Datasets produced here are tagged <c>source = "procedural"</c> and the app
    /// surfaces that in its provenance label — nothing is presented as observed.
    /// </summary>
    public static class ProceduralWeather
    {
        /// <summary>
        /// Axis position of the map centre for the default 20° tilt: with
        /// <c>axis = u*cos(0.35) + v*sin(0.35)</c> over u,v ∈ [0,1], the centre
        /// (u=v=0.5) sits at 0.5*(cos+sin) ≈ 0.6411. Centring the line here, rather
        /// than the old 0.45, puts the convective core and the densest cloud over
        /// the middle of the region instead of tucked into one corner.
        /// </summary>
        public const float DefaultPhase = 0.6411f;

        /// <summary>
        /// Builds a synthetic snapshot on a <paramref name="gridSize"/> square grid.
        /// </summary>
        /// <param name="phase">
        /// Position of the squall line across the region in the same units as
        /// <c>axis</c> below (roughly 0..1.28 for the default 20° tilt, not a plain
        /// 0..1 fraction), letting callers march the system across the map for an
        /// animated forecast timeline. Defaults to the map centre -- see
        /// <see cref="DefaultPhase"/> -- rather than 0.45, which put the convective
        /// core off to one side of the map (the map centre sits at axis ≈ 0.64 for
        /// the default tilt, not 0.45) and made the whole storm read as parked in
        /// one corner instead of over the region the map exists to show.
        /// </param>
        public static WeatherDataset Generate(GeoBounds bounds, int gridSize, int seed, float phase = DefaultPhase)
        {
            gridSize = Mathf.Clamp(gridSize, 4, 256);

            var dataset = new WeatherDataset
            {
                schemaVersion = WeatherDataset.CurrentSchemaVersion,
                source = "procedural",
                attribution = "Synthetic mesoscale convective system — not observed data.",
                observationTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                minLatitude = bounds.MinLatitude,
                maxLatitude = bounds.MaxLatitude,
                minLongitude = bounds.MinLongitude,
                maxLongitude = bounds.MaxLongitude,
                gridWidth = gridSize,
                gridHeight = gridSize,
                layers = WeatherDataset.DefaultLayers(),
                cells = new WeatherCell[gridSize * gridSize]
            };

            for (int y = 0; y < gridSize; y++)
            {
                float v = y / (float)(gridSize - 1);
                for (int x = 0; x < gridSize; x++)
                {
                    float u = x / (float)(gridSize - 1);
                    dataset.cells[y * gridSize + x] = CellAt(u, v, seed, phase);
                }
            }

            return dataset;
        }

        static WeatherCell CellAt(float u, float v, int seed, float phase)
        {
            // Squall line: a straight front tilted ~20° from north, advancing east.
            // `s` is signed distance ahead of (+) or behind (−) the line, in map units.
            const float tiltRadians = 0.35f;
            float axis = u * Mathf.Cos(tiltRadians) + v * Mathf.Sin(tiltRadians);
            // Waviness so the line is not a ruler-straight artefact.
            float waviness = 0.055f * (Noise.Fbm2(v * 3.1f, 7.3f, 3, 2f, 0.5f, seed + 5) - 0.5f) * 2f;
            float s = axis - (phase + waviness);

            // --- convective core -------------------------------------------
            // Band of towering cumulonimbus straddling the line. Widened from the
            // original ~4 km (sigma 0.055) to ~5-6 km so the core covers more of a
            // 5 km-wide region instead of reading as a thin ribbon crossing it.
            float core = Gaussian(s, 0.075f);
            // Broken into discrete cells along the line, as real squall lines are.
            float cellular = 0.55f + 0.45f * Noise.Fbm2(axis * 4f, (v - u * 0.3f) * 14f, 3, 2f, 0.55f, seed + 19);
            core *= cellular;

            // --- trailing stratiform shield --------------------------------
            // Broad region of layered cloud and steady rain behind the line: it ramps
            // in just behind the convective core and thins out towards the back edge.
            // Widened along with the core above so the shield still reads as
            // proportionate to it rather than suddenly narrow behind a wider line.
            float trailingRise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.07f, -0.27f, s));
            float trailingFade = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.55f, -1.05f, s));
            float trailing = trailingRise * (1f - trailingFade);

            // --- forward anvil ----------------------------------------------
            // Cirrus blown downshear, well ahead of the surface line, no rain under it.
            float anvil = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.30f, 0.02f, s))
                        * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.05f, 0.05f, s));

            // Fair-weather cumulus scattered across the undisturbed air mass.
            float fairWeather = Mathf.Max(0f,
                Noise.Fbm2(u * 6.5f, v * 6.5f, 4, 2f, 0.5f, seed + 61) - 0.52f) * 1.6f;

            // --- layer assembly ---------------------------------------------
            float low = Mathf.Clamp01(core * 0.95f + trailing * 0.55f + fairWeather * 0.7f);
            float mid = Mathf.Clamp01(core * 0.9f + trailing * 0.8f + fairWeather * 0.25f);
            float high = Mathf.Clamp01(core * 0.8f + trailing * 0.45f + anvil * 0.85f);

            // Total cover is the random-overlap combination of the three layers,
            // which is how ECMWF derives it: 1 − Π(1 − c_i).
            float total = 1f - (1f - low) * (1f - mid) * (1f - high);

            // --- precipitation ----------------------------------------------
            // Convective core delivers the heavy rates; the stratiform region gives a
            // long tail of light steady rain. Anvil cirrus precipitates nothing.
            float precip = core * 26f * cellular + trailing * 2.4f;
            precip = Mathf.Max(0f, precip - 0.15f); // trim the drizzle floor

            // --- instability -------------------------------------------------
            // CAPE peaks in the inflow just ahead of the line and is consumed behind it.
            float inflow = Gaussian(s - 0.09f, 0.13f);
            float wake = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.05f, -0.35f, s));
            float cape = 320f + 2600f * inflow * (0.7f + 0.3f * Noise.Fbm2(u * 5f, v * 5f, 3, 2f, 0.5f, seed + 88));
            cape *= 1f - 0.75f * wake;

            // --- surface fields ------------------------------------------------
            // Cold pool behind the line: several degrees of outflow-driven cooling.
            float temperature = 31.5f - 6.5f * wake - 2.0f * core
                              + 1.2f * (Noise.Fbm2(u * 4f, v * 4f, 3, 2f, 0.5f, seed + 133) - 0.5f) * 2f;

            // Prevailing south-westerly monsoon flow, with divergent outflow at the line.
            float windU = 6.2f + 9f * core;
            float windV = 4.4f - 3f * wake;

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
                windU = windU,
                windV = windV
            };
        }

        /// <summary>Unit-height Gaussian of standard deviation <paramref name="sigma"/>.</summary>
        static float Gaussian(float x, float sigma)
        {
            float t = x / sigma;
            return Mathf.Exp(-0.5f * t * t);
        }
    }
}

using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// Fallback terrain for when no baked <c>terrain.bin</c> is present.
    ///
    /// This is a stylised stand-in for the Yangtze delta rather than a guess at real
    /// elevations: the East China Sea to the east, the Yangtze estuary opening to the
    /// north-east, a meandering Huangpu, an almost dead-flat alluvial plain a few
    /// metres above sea level, and the low residual hills to the south-west (the real
    /// She Shan tops out under 100 m). It exists so the app is never a black screen,
    /// and the app labels the map "procedural" whenever it is used.
    /// </summary>
    public static class ProceduralTerrain
    {
        public const float SeaFloorElevation = -18f;
        public const float PlainElevation = 4f;
        public const float HillPeakElevation = 96f;

        public static TerrainHeightfield Generate(GeoBounds bounds, int resolution, int seed)
        {
            resolution = Mathf.Clamp(resolution, 32, 2048);
            var field = new TerrainHeightfield(
                resolution, resolution, bounds, SeaFloorElevation, HillPeakElevation);

            for (int y = 0; y < resolution; y++)
            {
                float v = y / (float)(resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    float u = x / (float)(resolution - 1);
                    field.SetElevation(x, y, ElevationAt(u, v, seed));
                }
            }

            return field;
        }

        /// <summary>Elevation in metres at normalised map coordinates (u east, v north).</summary>
        public static float ElevationAt(float u, float v, int seed)
        {
            // --- coastline -------------------------------------------------
            // The coast runs roughly NNW-SSE through the eastern third of the tile,
            // wandering with low-frequency noise so it never reads as a straight edge.
            float coastX = 0.72f + 0.05f * (Noise.Fbm2(v * 2.4f, 11.7f, 3, 2f, 0.5f, seed) - 0.5f) * 2f;
            // The estuary flares open towards the north.
            coastX -= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.62f, 1f, v)) * 0.28f;

            float landMask = Mathf.SmoothStep(0f, 1f, (coastX - u) / 0.045f);

            // --- alluvial plain -------------------------------------------
            // Delta relief is genuinely tiny: a few metres of levees and fill.
            float plainRelief = (Noise.Fbm2(u * 7f, v * 7f, 4, 2f, 0.5f, seed + 31) - 0.5f) * 6f;
            float land = PlainElevation + plainRelief;

            // --- south-western hills ---------------------------------------
            // A cluster of isolated low hills, not a range: separate Gaussian bumps
            // modulated by noise.
            float hills = 0f;
            hills += Bump(u, v, 0.17f, 0.21f, 0.075f) * 96f;
            hills += Bump(u, v, 0.28f, 0.13f, 0.055f) * 61f;
            hills += Bump(u, v, 0.09f, 0.36f, 0.048f) * 44f;
            hills += Bump(u, v, 0.34f, 0.30f, 0.040f) * 33f;
            hills *= 0.65f + 0.35f * Noise.Fbm2(u * 18f, v * 18f, 3, 2f, 0.5f, seed + 77);
            land += hills;

            // --- rivers -----------------------------------------------------
            // Huangpu: a meander running SW -> NE across the plain into the estuary.
            float huangpu = RiverMask(u, v, seed + 512, amplitude: 0.10f, frequency: 2.3f,
                                      baseline: 0.36f, slope: 0.42f, halfWidth: 0.016f);
            // A smaller tributary (Suzhou Creek) running roughly west -> east.
            float creek = RiverMask(u, v, seed + 913, amplitude: 0.045f, frequency: 4.1f,
                                    baseline: 0.60f, slope: 0.04f, halfWidth: 0.008f);
            float river = Mathf.Max(huangpu, creek);
            land = Mathf.Lerp(land, -6f, river);

            // --- sea floor ---------------------------------------------------
            // Shallow shelf that deepens gradually offshore, with a dredged channel
            // scoured out along the estuary axis.
            float offshore = Mathf.Clamp01((u - coastX) / 0.30f);
            float sea = Mathf.Lerp(-2f, SeaFloorElevation, Mathf.SmoothStep(0f, 1f, offshore));
            sea += (Noise.Fbm2(u * 9f, v * 9f, 3, 2f, 0.5f, seed + 404) - 0.5f) * 3f;

            return Mathf.Lerp(sea, land, landMask);
        }

        /// <summary>
        /// A hill: smooth radial falloff, squared so the flanks are convex rather
        /// than conical.
        /// </summary>
        static float Bump(float u, float v, float cx, float cy, float radius)
        {
            float d = Mathf.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) / radius;
            if (d >= 1f) return 0f;
            float t = 1f - d;
            return t * t;
        }

        /// <summary>
        /// 0..1 mask for a sinuous river channel. The centreline is a sine meander
        /// with a linear drift, perturbed by noise so it is not obviously periodic.
        /// </summary>
        static float RiverMask(float u, float v, int seed, float amplitude, float frequency,
                               float baseline, float slope, float halfWidth)
        {
            float centre = baseline + slope * v
                         + amplitude * Mathf.Sin(v * frequency * Mathf.PI * 2f)
                         + amplitude * 0.6f * (Noise.Fbm2(v * 3.5f, seed * 0.013f, 3, 2f, 0.5f, seed) - 0.5f) * 2f;

            float d = Mathf.Abs(u - centre);
            // Channels widen downstream.
            float width = halfWidth * (0.6f + 0.8f * v);
            return 1f - Mathf.SmoothStep(width, width * 2.4f, d);
        }
    }
}

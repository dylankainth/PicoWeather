using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// Synthesises a true-colour-looking basemap from a heightfield, for when no
    /// baked <c>satellite.jpg</c> is present.
    ///
    /// This is landcover painting, not imagery: water is tinted by depth and by the
    /// heavy sediment load that makes the Yangtze estuary famously brown, the delta
    /// plain is a patchwork of paddy and field parcels, the urban core is a grey
    /// mass concentrated near the river confluence, and the south-western hills are
    /// wooded. The result reads correctly at tabletop scale and is clearly labelled
    /// procedural in the app.
    /// </summary>
    public static class ProceduralSatellite
    {
        static readonly Color32 DeepWater = new Color32(24, 46, 68, 255);
        static readonly Color32 ShallowWater = new Color32(52, 78, 92, 255);
        static readonly Color32 SedimentWater = new Color32(118, 104, 76, 255);
        static readonly Color32 Wetland = new Color32(92, 100, 66, 255);
        static readonly Color32 Cropland = new Color32(104, 122, 66, 255);
        static readonly Color32 CroplandAlt = new Color32(132, 138, 84, 255);
        static readonly Color32 Forest = new Color32(56, 82, 48, 255);
        static readonly Color32 UrbanCore = new Color32(126, 124, 122, 255);
        static readonly Color32 Suburb = new Color32(112, 112, 100, 255);

        public static Texture2D Generate(TerrainHeightfield field, int resolution, int seed)
        {
            resolution = Mathf.Clamp(resolution, 128, 4096);

            var texture = new Texture2D(resolution, resolution, TextureFormat.RGB24,
                                        mipChain: true, linear: false)
            {
                name = "SatelliteBasemap (procedural)",
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4
            };

            var pixels = new Color32[resolution * resolution];

            for (int y = 0; y < resolution; y++)
            {
                float v = y / (float)(resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    float u = x / (float)(resolution - 1);
                    float elevation = field != null
                        ? field.SampleElevation(u, v)
                        : ProceduralTerrain.ElevationAt(u, v, seed);
                    pixels[y * resolution + x] = Shade(u, v, elevation, seed);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return texture;
        }

        static Color32 Shade(float u, float v, float elevation, int seed)
        {
            // ------------------------------------------------------------ water
            if (elevation < 0f)
            {
                float depth = Mathf.Clamp01(-elevation / 18f);
                Color water = Color32.Lerp(ShallowWater, DeepWater, Mathf.SmoothStep(0f, 1f, depth));

                // Sediment plume: strongest close inshore and along the estuary axis,
                // dispersing offshore.
                float plume = Mathf.Clamp01(1f - depth * 1.6f);
                plume *= 0.55f + 0.45f * Noise.Fbm2(u * 9f, v * 9f, 4, 2f, 0.5f, seed + 211);
                water = Color.Lerp(water, SedimentWater, plume * 0.85f);

                return Jitter(water, u, v, seed + 9, 0.03f);
            }

            // ---------------------------------------------------------- wetland
            if (elevation < 1.5f)
            {
                float t = Mathf.InverseLerp(0f, 1.5f, elevation);
                return Jitter(Color32.Lerp(SedimentWater, Wetland, t), u, v, seed + 17, 0.04f);
            }

            // ------------------------------------------------------------ hills
            if (elevation > 24f)
            {
                float t = Mathf.Clamp01((elevation - 24f) / 60f);
                Color wooded = Color.Lerp((Color)Cropland, (Color)Forest, Mathf.SmoothStep(0f, 1f, t));
                // Slope shading picked up from the fine noise gives the hills relief.
                float texture = Noise.Fbm2(u * 60f, v * 60f, 3, 2f, 0.5f, seed + 303);
                wooded *= 0.85f + 0.3f * texture;
                return Jitter(wooded, u, v, seed + 23, 0.05f);
            }

            // ------------------------------------------------------------ urban
            // Density falls off from the historic centre on the Huangpu, with a
            // secondary cluster around the estuary port.
            float urban = 0f;
            urban += RadialFalloff(u, v, 0.52f, 0.50f, 0.135f) * 1.0f;
            urban += RadialFalloff(u, v, 0.63f, 0.63f, 0.075f) * 0.7f;
            urban += RadialFalloff(u, v, 0.44f, 0.38f, 0.060f) * 0.5f;
            // Ribbon development along the transport corridors, as fine filaments.
            urban += Mathf.Max(0f, Noise.Fbm2(u * 14f, v * 14f, 3, 2f, 0.5f, seed + 41) - 0.60f) * 2.2f;
            urban = Mathf.Clamp01(urban);

            // ------------------------------------------------------- agriculture
            // Field parcels: quantised noise gives the patchwork of distinct plots
            // that makes farmland recognisable from orbit.
            const float parcelsPerEdge = 46f;
            float px = Mathf.Floor(u * parcelsPerEdge);
            float py = Mathf.Floor(v * parcelsPerEdge);
            float parcel = Noise.Perlin2(px * 0.73f, py * 0.73f, seed + 57);
            Color farmland = Color.Lerp((Color)Cropland, (Color)CroplandAlt, parcel);
            // Faint hedgerow/bund lines between parcels.
            float edgeU = Mathf.Abs(Mathf.Repeat(u * parcelsPerEdge, 1f) - 0.5f) * 2f;
            float edgeV = Mathf.Abs(Mathf.Repeat(v * parcelsPerEdge, 1f) - 0.5f) * 2f;
            float bund = Mathf.Max(edgeU, edgeV);
            farmland *= Mathf.Lerp(1f, 0.9f, Mathf.SmoothStep(0.86f, 1f, bund));

            Color built = Color.Lerp((Color)Suburb, (Color)UrbanCore, urban);
            Color surface = Color.Lerp(farmland, built, Mathf.SmoothStep(0f, 1f, urban));

            return Jitter(surface, u, v, seed + 29, 0.035f);
        }

        static float RadialFalloff(float u, float v, float cx, float cy, float radius)
        {
            float d = Mathf.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) / radius;
            return d >= 1f ? 0f : Mathf.SmoothStep(1f, 0f, d);
        }

        /// <summary>Per-pixel grain, so large flat areas do not band after JPEG-free upload.</summary>
        static Color32 Jitter(Color color, float u, float v, int seed, float amount)
        {
            float n = Noise.Perlin2(u * 512f, v * 512f, seed) - 0.5f;
            float k = 1f + n * amount * 2f;
            return new Color32(
                (byte)Mathf.Clamp(color.r * 255f * k, 0f, 255f),
                (byte)Mathf.Clamp(color.g * 255f * k, 0f, 255f),
                (byte)Mathf.Clamp(color.b * 255f * k, 0f, 255f),
                255);
        }
    }
}

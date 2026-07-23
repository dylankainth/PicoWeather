using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Clouds
{
    /// <summary>
    /// Turns the weather grid into the two 3D textures the cloud shader consumes.
    ///
    /// The division of labour matters: the *density volume* carries the real
    /// meteorology — which layers are covered, over which part of the map, between
    /// which altitudes — while the *detail volume* carries only appearance. A 31 km
    /// weather model cannot know the shape of an individual cumulus tower, so
    /// inventing one in the density volume would be dressing fiction up as data.
    /// Keeping them separate means the honest part stays honest.
    /// </summary>
    public static class CloudVolumeBuilder
    {
        /// <summary>Lattice cells per unit for the tiling detail noise.</summary>
        const int DetailBaseFrequency = 4;

        /// <summary>
        /// Builds the base density volume. X is east, Y is altitude (0 at the
        /// atmosphere floor, 1 at the ceiling), Z is north — matching the object
        /// space of the cloud box.
        /// </summary>
        public static Texture3D BuildDensityVolume(WeatherDataset weather, AppConfig config)
        {
            int width = Mathf.Clamp(config.CloudVolumeXZ, 16, 128);
            int depth = width;
            int height = Mathf.Clamp(config.CloudVolumeY, 8, 64);

            var texture = new Texture3D(width, height, depth, TextureFormat.R8, mipChain: false)
            {
                name = "CloudDensityVolume",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };

            var pixels = new Color32[width * height * depth];
            float floor = config.AtmosphereFloorMeters;
            float ceiling = config.AtmosphereCeilingMeters;
            int seed = config.ProceduralSeed;

            // Resolve the three layers' altitude windows once.
            var layers = new (float baseAlt, float topAlt, Atmosphere.Layer id)[Atmosphere.LayerCount];
            for (int l = 0; l < Atmosphere.LayerCount; l++)
            {
                var id = (Atmosphere.Layer)l;
                var meta = weather.LayerFor(id);
                layers[l] = (meta.baseAltitudeM, meta.topAltitudeM, id);
            }

            for (int z = 0; z < depth; z++)
            {
                float v = z / (float)(depth - 1);
                for (int x = 0; x < width; x++)
                {
                    float u = x / (float)(width - 1);

                    // Sample the weather grid once per column rather than per voxel.
                    float coverLow = weather.SampleBilinear(u, v, c => c.cloudLow);
                    float coverMid = weather.SampleBilinear(u, v, c => c.cloudMid);
                    float coverHigh = weather.SampleBilinear(u, v, c => c.cloudHigh);

                    for (int y = 0; y < height; y++)
                    {
                        float altitude = Mathf.Lerp(floor, ceiling, y / (float)(height - 1));

                        float density = 0f;
                        for (int l = 0; l < Atmosphere.LayerCount; l++)
                        {
                            float coverage = l == 0 ? coverLow : (l == 1 ? coverMid : coverHigh);
                            if (coverage <= 0.01f) continue;

                            float profile = LayerProfile(altitude, layers[l].baseAlt, layers[l].topAlt,
                                                         coverage, layers[l].id);
                            if (profile <= 0f) continue;

                            // Layers stack rather than sum: two overlapping decks are
                            // not twice as opaque as one.
                            density = Mathf.Max(density, coverage * profile);
                        }

                        if (density > 0f)
                        {
                            // Medium-frequency structure the weather model is too coarse
                            // to resolve: breaks a flat deck into discrete cells. The
                            // shader adds the fine detail on top of this.
                            float structure = Noise.Fbm3(u * 3.5f, altitude / 2200f, v * 3.5f,
                                                         4, 2f, 0.5f, seed + 401);
                            density *= Mathf.Clamp01(0.45f + 1.1f * structure);
                        }

                        int index = x + y * width + z * width * height;
                        byte value = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(density) * 255f), 0, 255);
                        pixels[index] = new Color32(value, value, value, value);
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }

        /// <summary>
        /// Vertical fill profile of one cloud layer.
        ///
        /// Clouds do not fill their whole pressure band: a 20 %-covered low deck is a
        /// thin sheet near the condensation level, while a fully covered one is deep.
        /// So the cloud top scales with coverage, the base is soft, and the top is
        /// sharper — which is what real cloud tops look like.
        /// </summary>
        static float LayerProfile(float altitude, float baseAltitude, float topAltitude,
                                  float coverage, Atmosphere.Layer layer)
        {
            if (topAltitude <= baseAltitude) return 0f;

            // High cloud (cirrus) is thin and sits near the top of its band; low and
            // mid cloud build up from their base.
            float fillLow, fillHigh;
            if (layer == Atmosphere.Layer.High)
            {
                fillLow = Mathf.Lerp(0.55f, 0.25f, coverage);
                fillHigh = Mathf.Lerp(0.75f, 1.0f, coverage);
            }
            else
            {
                fillLow = 0f;
                fillHigh = Mathf.Lerp(0.30f, 1.0f, coverage);
            }

            float cloudBase = Mathf.Lerp(baseAltitude, topAltitude, fillLow);
            float cloudTop = Mathf.Lerp(baseAltitude, topAltitude, fillHigh);
            if (cloudTop <= cloudBase) return 0f;

            float t = Mathf.InverseLerp(cloudBase, cloudTop, altitude);
            if (t <= 0f || t >= 1f) return 0f;

            // Soft base, firm top.
            float rise = Mathf.SmoothStep(0f, 0.22f, t);
            float fall = 1f - Mathf.SmoothStep(0.82f, 1f, t);
            return rise * fall;
        }

        /// <summary>
        /// Builds the tiling detail volume. This is expensive (a 64³ volume is
        /// 262 144 Worley evaluations) and completely independent of the weather, so
        /// it is generated once and cached rather than rebuilt per snapshot.
        /// </summary>
        public static Texture3D BuildDetailVolume(int resolution, int seed)
        {
            resolution = Mathf.Clamp(resolution, 16, 64);

            var texture = new Texture3D(resolution, resolution, resolution, TextureFormat.R8, mipChain: false)
            {
                name = "CloudDetailVolume",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0
            };

            var pixels = new Color32[resolution * resolution * resolution];

            for (int z = 0; z < resolution; z++)
            {
                float fz = z / (float)resolution;
                for (int y = 0; y < resolution; y++)
                {
                    float fy = y / (float)resolution;
                    for (int x = 0; x < resolution; x++)
                    {
                        float fx = x / (float)resolution;
                        float n = Noise.CloudDetailPeriodic(fx, fy, fz, DetailBaseFrequency, seed);
                        byte value = (byte)Mathf.Clamp(Mathf.RoundToInt(n * 255f), 0, 255);
                        pixels[x + y * resolution + z * resolution * resolution] =
                            new Color32(value, value, value, value);
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return texture;
        }

        static Texture3D _cachedDetail;
        static int _cachedDetailKey;

        /// <summary>Detail volume, generated on first use and shared thereafter.</summary>
        public static Texture3D GetOrCreateDetailVolume(int resolution, int seed)
        {
            int key = resolution * 397 ^ seed;
            if (_cachedDetail != null && _cachedDetailKey == key) return _cachedDetail;

            _cachedDetail = BuildDetailVolume(resolution, seed);
            _cachedDetailKey = key;
            return _cachedDetail;
        }
    }
}

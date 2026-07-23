using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// Deterministic gradient noise used by every procedural fallback.
    ///
    /// Unity's <c>Mathf.PerlinNoise</c> is not guaranteed stable across versions or
    /// platforms and only exists in 2D, so we hash our own. Everything here is a
    /// pure function of (position, seed): the same seed produces the same weather
    /// on the editor and on device, which is what makes the demo repeatable.
    /// </summary>
    public static class Noise
    {
        // ------------------------------------------------------------- hashing

        static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352du;
            x ^= x >> 15; x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        static uint Hash(int x, int y, int seed)
            => Hash((uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^ (uint)seed * 0xC2B2AE3Du);

        static uint Hash(int x, int y, int z, int seed)
            => Hash((uint)x * 0x9E3779B1u ^ (uint)y * 0x85EBCA77u ^
                    (uint)z * 0xC2B2AE3Du ^ (uint)seed * 0x27D4EB2Fu);

        /// <summary>Uniform 0..1 from an integer lattice point.</summary>
        static float Unit(uint h) => (h & 0x00FFFFFFu) / (float)0x01000000u;

        static Vector2 Gradient2(int x, int y, int seed)
        {
            float angle = Unit(Hash(x, y, seed)) * Mathf.PI * 2f;
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        }

        static Vector3 Gradient3(int x, int y, int z, int seed)
        {
            uint h = Hash(x, y, z, seed);
            float theta = Unit(h) * Mathf.PI * 2f;
            float cosPhi = Unit(Hash(h ^ 0x5F356495u)) * 2f - 1f;
            float sinPhi = Mathf.Sqrt(Mathf.Max(0f, 1f - cosPhi * cosPhi));
            return new Vector3(Mathf.Cos(theta) * sinPhi, Mathf.Sin(theta) * sinPhi, cosPhi);
        }

        /// <summary>Quintic smoothstep — C2 continuous, so fBm has no visible lattice creases.</summary>
        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        // --------------------------------------------------------------- perlin

        /// <summary>Perlin gradient noise in 2D, returned in 0..1.</summary>
        public static float Perlin2(float x, float y, int seed = 0)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float xf = x - xi, yf = y - yi;
            float u = Fade(xf), v = Fade(yf);

            float d00 = Vector2.Dot(Gradient2(xi, yi, seed), new Vector2(xf, yf));
            float d10 = Vector2.Dot(Gradient2(xi + 1, yi, seed), new Vector2(xf - 1f, yf));
            float d01 = Vector2.Dot(Gradient2(xi, yi + 1, seed), new Vector2(xf, yf - 1f));
            float d11 = Vector2.Dot(Gradient2(xi + 1, yi + 1, seed), new Vector2(xf - 1f, yf - 1f));

            float a = Mathf.Lerp(d00, d10, u);
            float b = Mathf.Lerp(d01, d11, u);
            return Mathf.Clamp01(Mathf.Lerp(a, b, v) * 0.7071f + 0.5f);
        }

        /// <summary>Perlin gradient noise in 3D, returned in 0..1.</summary>
        public static float Perlin3(float x, float y, float z, int seed = 0)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
            float xf = x - xi, yf = y - yi, zf = z - zi;
            float u = Fade(xf), v = Fade(yf), w = Fade(zf);

            float Dot(int cx, int cy, int cz) => Vector3.Dot(
                Gradient3(xi + cx, yi + cy, zi + cz, seed),
                new Vector3(xf - cx, yf - cy, zf - cz));

            float x00 = Mathf.Lerp(Dot(0, 0, 0), Dot(1, 0, 0), u);
            float x10 = Mathf.Lerp(Dot(0, 1, 0), Dot(1, 1, 0), u);
            float x01 = Mathf.Lerp(Dot(0, 0, 1), Dot(1, 0, 1), u);
            float x11 = Mathf.Lerp(Dot(0, 1, 1), Dot(1, 1, 1), u);

            float y0 = Mathf.Lerp(x00, x10, v);
            float y1 = Mathf.Lerp(x01, x11, v);
            return Mathf.Clamp01(Mathf.Lerp(y0, y1, w) * 0.8660f + 0.5f);
        }

        // ------------------------------------------------------------------ fBm

        /// <summary>Fractional Brownian motion over <see cref="Perlin2"/>, 0..1.</summary>
        public static float Fbm2(float x, float y, int octaves = 4, float lacunarity = 2f,
                                 float gain = 0.5f, int seed = 0)
        {
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Perlin2(x * freq, y * freq, seed + i * 131);
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>Fractional Brownian motion over <see cref="Perlin3"/>, 0..1.</summary>
        public static float Fbm3(float x, float y, float z, int octaves = 4, float lacunarity = 2f,
                                 float gain = 0.5f, int seed = 0)
        {
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Perlin3(x * freq, y * freq, z * freq, seed + i * 131);
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        /// <summary>
        /// Worley/cellular noise, returned as 1 − distance-to-nearest-feature so that
        /// high values sit at the cell centres. Used to give cloud volumes their
        /// billowy, cauliflower-edged look rather than the smooth blobs fBm alone gives.
        /// </summary>
        public static float Worley3(float x, float y, float z, int seed = 0)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y), zi = Mathf.FloorToInt(z);
            float best = 1e9f;

            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = xi + dx, cy = yi + dy, cz = zi + dz;
                uint h = Hash(cx, cy, cz, seed);
                var feature = new Vector3(
                    cx + Unit(h),
                    cy + Unit(Hash(h ^ 0x68bc21ebu)),
                    cz + Unit(Hash(h ^ 0x02e5be93u)));
                float d = (feature - new Vector3(x, y, z)).sqrMagnitude;
                if (d < best) best = d;
            }

            return Mathf.Clamp01(1f - Mathf.Sqrt(best));
        }

        /// <summary>Multi-octave Worley, inverted so it reads as cloud billows.</summary>
        public static float WorleyFbm3(float x, float y, float z, int octaves = 3, int seed = 0)
        {
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Worley3(x * freq, y * freq, z * freq, seed + i * 977);
                norm += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            return norm > 0f ? sum / norm : 0f;
        }
    }
}

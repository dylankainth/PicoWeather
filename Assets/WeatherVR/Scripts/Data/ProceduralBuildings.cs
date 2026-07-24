using System.Collections.Generic;
using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// Fallback buildings for when no baked <c>buildings.json</c> is present.
    ///
    /// Not an attempt at real London geometry -- a stylised city block grid: streets
    /// left clear at low "presence" noise, one landmark tower at the region's centre
    /// standing in for the Gherkin without claiming to be it, and heights varying by
    /// the same seeded noise every other procedural layer uses. It exists so the app
    /// is never a bare satellite plate if the bake or the query failed, and it is
    /// always labelled "procedural" on screen.
    /// </summary>
    public static class ProceduralBuildings
    {
        const int GridSize = 7;
        const float MinHeightMeters = 8f;
        const float MaxHeightMeters = 140f;
        const float LandmarkHeightMeters = 180f;

        public static BuildingDataset Generate(GeoBounds bounds, int seed)
        {
            var records = new List<BuildingRecord>();
            int center = GridSize / 2;

            for (int gy = 0; gy < GridSize; gy++)
            {
                for (int gx = 0; gx < GridSize; gx++)
                {
                    // Leave the outer ring clear so buildings don't crowd the map edge.
                    if (gx == 0 || gy == 0 || gx == GridSize - 1 || gy == GridSize - 1) continue;

                    float cellU = (gx + 0.5f) / GridSize;
                    float cellV = (gy + 0.5f) / GridSize;

                    // Some cells stay empty -- streets and plazas, deterministically.
                    float presence = Noise.Fbm2(gx * 3.1f, gy * 3.1f, 2, 2f, 0.5f, seed + 401);
                    bool landmark = gx == center && gy == center;
                    if (presence < 0.22f && !landmark) continue;

                    float sizeT = 0.55f + 0.25f * Noise.Fbm2(gx * 1.7f, gy * 1.7f, 2, 2f, 0.5f, seed + 71);
                    float halfW = sizeT * 0.5f / GridSize;
                    float halfH = sizeT * 0.42f / GridSize;
                    float yaw = (Noise.Fbm2(gx * 5.3f, gy * 5.3f, 2, 2f, 0.5f, seed + 909) - 0.5f) * 40f * Mathf.Deg2Rad;

                    float height = landmark
                        ? LandmarkHeightMeters
                        : Mathf.Lerp(MinHeightMeters, MaxHeightMeters,
                            Noise.Fbm2(gx * 2.3f, gy * 2.3f, 3, 2f, 0.5f, seed + 137));

                    records.Add(BuildRectangle(bounds, cellU, cellV, halfW, halfH, yaw, height));
                }
            }

            return new BuildingDataset
            {
                source = "procedural",
                attribution = "Procedural city block layout (no real building data).",
                minLatitude = bounds.MinLatitude,
                maxLatitude = bounds.MaxLatitude,
                minLongitude = bounds.MinLongitude,
                maxLongitude = bounds.MaxLongitude,
                buildings = records.ToArray()
            };
        }

        /// <summary>
        /// An axis-aligned rectangle rotated by <paramref name="yaw"/> about its own
        /// centre, in normalised map space, then projected to lat/lon. Corners are
        /// wound counter-clockwise in (u, v) -- and therefore in local (x, z), since
        /// that projection is a plain scale-and-translate with no reflection -- to
        /// match the winding <see cref="Terrain.BuildingMeshBuilder"/> assumes.
        /// </summary>
        static BuildingRecord BuildRectangle(GeoBounds bounds, float centerU, float centerV,
                                             float halfW, float halfH, float yaw, float heightMeters)
        {
            Vector2[] corners =
            {
                new Vector2(-halfW, -halfH),
                new Vector2( halfW, -halfH),
                new Vector2( halfW,  halfH),
                new Vector2(-halfW,  halfH),
            };

            float cos = Mathf.Cos(yaw), sin = Mathf.Sin(yaw);
            var flat = new double[corners.Length * 2];
            for (int i = 0; i < corners.Length; i++)
            {
                Vector2 p = corners[i];
                float u = centerU + p.x * cos - p.y * sin;
                float v = centerV + p.x * sin + p.y * cos;
                bounds.FromNormalized(Mathf.Clamp01(u), Mathf.Clamp01(v), out double lat, out double lon);
                flat[i * 2] = lat;
                flat[i * 2 + 1] = lon;
            }

            return new BuildingRecord { heightMeters = heightMeters, footprintFlat = flat };
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Terrain
{
    /// <summary>
    /// Extrudes every building footprint into one combined mesh in local map space:
    /// X and Z span [-0.5, 0.5], Y is VR metres above the map plane -- the same
    /// convention <see cref="TerrainMeshBuilder"/> uses. One mesh for every building
    /// rather than one GameObject each, because a 600-building cap at one draw call
    /// each would blow the performance budget before a single cloud voxel renders.
    ///
    /// Winding: footprints arrive counter-clockwise as seen from above (both the
    /// bake and <see cref="ProceduralBuildings"/> guarantee this). Wall and roof
    /// triangle order below were derived directly from -- and checked against --
    /// <see cref="TerrainMeshBuilder"/>'s own documented rule that a front-facing
    /// triangle (v0, v1, v2) has normal <c>Cross(v1 - v0, v2 - v0)</c>.
    ///
    /// Vertex UV carries building semantics for the facade shader:
    ///   u = the building's true height in metres (constant across its vertices)
    ///   v = height in floors from the base (0 at the base, one unit per ~3 m storey,
    ///       up to the roof) rather than a plain 0..1 fraction, so a bare frac(v) in
    ///       the shader already bands once per floor with no further scaling
    /// </summary>
    public static class BuildingMeshBuilder
    {
        const float MetresPerFloorUvHint = 3f;

        public static Mesh Build(BuildingDataset dataset, TerrainHeightfield terrainField,
                                 GeoBounds mapBounds, AppConfig config, int maxBuildings,
                                 out int builtCount, out int droppedCount)
        {
            builtCount = 0;
            droppedCount = 0;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color32>();
            var triangles = new List<int>();

            if (dataset?.buildings == null) return null;

            int limit = Mathf.Min(dataset.buildings.Length, Mathf.Max(0, maxBuildings));
            droppedCount = dataset.buildings.Length - limit;

            for (int b = 0; b < limit; b++)
            {
                BuildingRecord record = dataset.buildings[b];
                if (record == null || record.PointCount < 3) continue;
                float heightMeters = Mathf.Max(record.heightMeters, 1f);

                int n = record.PointCount;
                var bottom = new Vector3[n];
                var top = new Vector3[n];
                Vector3 centroid = Vector3.zero;

                for (int i = 0; i < n; i++)
                {
                    double lat = record.LatitudeAt(i);
                    double lon = record.LongitudeAt(i);

                    float elevation = 0f;
                    if (terrainField != null)
                    {
                        Vector2 uv01 = mapBounds.ToNormalized(lat, lon);
                        elevation = terrainField.SampleElevation(uv01.x, uv01.y);
                    }
                    float baseY = config.TerrainElevationToMapUnits(elevation);
                    float topY = baseY + config.BuildingHeightToMapUnits(heightMeters);

                    Vector3 local = mapBounds.ToLocal(lat, lon);
                    bottom[i] = new Vector3(local.x, baseY, local.z);
                    top[i] = new Vector3(local.x, topY, local.z);
                    centroid += top[i];
                }
                centroid /= n;

                // A deterministic tint per building, so a skyline of identical grey
                // boxes still reads as individual buildings rather than one slab.
                uint hash = (uint)(b * 2654435761u) ^ (uint)(heightMeters * 97f);
                float shade = 0.46f + ((hash >> 8) & 0xFF) / 255f * 0.16f;
                var tint = new Color32(
                    (byte)(shade * 255f), (byte)(shade * 255f), (byte)((shade + 0.02f) * 255f), 255);

                float floors = heightMeters / MetresPerFloorUvHint;

                // Walls: one quad per footprint edge, its own four vertices so the
                // recalculated normal is flat per face rather than averaged at corners.
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;

                    int v0 = vertices.Count;
                    AddWallVertex(bottom[i], 0f, heightMeters, floors, tint, vertices, uvs, colors);
                    int v1 = vertices.Count;
                    AddWallVertex(top[j], 1f, heightMeters, floors, tint, vertices, uvs, colors);
                    int v2 = vertices.Count;
                    AddWallVertex(bottom[j], 0f, heightMeters, floors, tint, vertices, uvs, colors);
                    int v3 = vertices.Count;
                    AddWallVertex(top[i], 1f, heightMeters, floors, tint, vertices, uvs, colors);

                    // (bottom_i, top_j, bottom_j) and (bottom_i, top_i, top_j): both
                    // resolve to Cross(...) pointing away from the footprint's
                    // interior for a counter-clockwise-from-above ring.
                    triangles.Add(v0); triangles.Add(v1); triangles.Add(v2);
                    triangles.Add(v0); triangles.Add(v3); triangles.Add(v1);
                }

                // Roof: a centroid fan. Reversed perimeter order relative to the
                // footprint's own winding is what turns the fan to face +Y.
                int centroidIndex = vertices.Count;
                AddWallVertex(centroid, 1f, heightMeters, floors, tint, vertices, uvs, colors);

                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    int pj = vertices.Count;
                    AddWallVertex(top[j], 1f, heightMeters, floors, tint, vertices, uvs, colors);
                    int pi = vertices.Count;
                    AddWallVertex(top[i], 1f, heightMeters, floors, tint, vertices, uvs, colors);

                    triangles.Add(centroidIndex); triangles.Add(pj); triangles.Add(pi);
                }

                builtCount++;
            }

            if (vertices.Count == 0) return null;

            var mesh = new Mesh { name = "BuildingsMesh" };
            mesh.indexFormat = vertices.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: false);

            return mesh;
        }

        static void AddWallVertex(Vector3 position, float heightFraction, float heightMeters,
                                  float floors, Color32 tint,
                                  List<Vector3> vertices, List<Vector2> uvs, List<Color32> colors)
        {
            vertices.Add(position);
            uvs.Add(new Vector2(heightMeters, heightFraction * floors));
            colors.Add(tint);
        }
    }
}

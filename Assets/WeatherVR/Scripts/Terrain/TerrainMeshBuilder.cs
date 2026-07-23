using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Terrain
{
    /// <summary>
    /// Turns a <see cref="TerrainHeightfield"/> into a renderable mesh in local map
    /// space: X and Z span [-0.5, 0.5], Y is VR metres above the map plane.
    ///
    /// Two decisions worth knowing about:
    ///
    /// * Bathymetry is clamped to zero for geometry. The sea surface is flat in
    ///   reality, and rendering the sea floor as terrain would push the East China
    ///   Sea metres below the table. Depth survives in the vertex colour so the
    ///   shader can still tint shallow water differently from deep.
    /// * Relief gets its own exaggeration on top of the atmosphere's. Real relief
    ///   here is tens of metres across 50 km — at true scale the delta is a
    ///   perfectly flat sheet, which reads as a bug rather than as geography.
    /// </summary>
    public static class TerrainMeshBuilder
    {
        /// <summary>
        /// Vertex colour channels, so the shader and this builder agree:
        ///   r = water mask (1 at or below sea level)
        ///   g = normalised elevation across the field's own range
        ///   b = normalised slope
        ///   a = shoreline proximity (1 right at the coast, falling off inland/offshore)
        /// </summary>
        public static class VertexChannel
        {
            public const string Description = "r=water, g=elevation, b=slope, a=shore";
        }

        /// <summary>
        /// Builds the mesh. <paramref name="resolution"/> is vertices per side;
        /// <paramref name="stride"/> lets callers subsample the heightfield for LODs.
        /// </summary>
        public static Mesh Build(TerrainHeightfield field, int resolution, AppConfig config, string name = "TerrainMesh")
        {
            if (field == null) return null;

            resolution = Mathf.Clamp(resolution, 4, 256);
            float reliefScale = config.VerticalScale * config.TerrainReliefExaggeration;

            int vertexCount = resolution * resolution;
            var vertices = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var colors = new Color32[vertexCount];

            // Sample once into a scratch grid: the slope pass needs neighbours, and
            // re-sampling the heightfield bilinearly four times per vertex is wasteful.
            var elevations = new float[vertexCount];
            for (int y = 0; y < resolution; y++)
            {
                float v = y / (float)(resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    float u = x / (float)(resolution - 1);
                    elevations[y * resolution + x] = field.SampleElevation(u, v);
                }
            }

            // Horizontal distance between adjacent vertices, in real metres — needed
            // to make the slope channel a true gradient rather than a per-resolution
            // artefact.
            float sampleSpacingMeters = (float)(field.Bounds.WidthMeters / (resolution - 1));
            float range = field.ElevationRange;

            for (int y = 0; y < resolution; y++)
            {
                float v = y / (float)(resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    int i = y * resolution + x;
                    float u = x / (float)(resolution - 1);
                    float elevation = elevations[i];

                    // Sea surface is flat; the sea floor lives only in the colour.
                    float renderElevation = Mathf.Max(elevation, 0f);
                    vertices[i] = new Vector3(u - 0.5f, renderElevation * reliefScale, v - 0.5f);
                    uvs[i] = new Vector2(u, v);

                    float left = elevations[y * resolution + Mathf.Max(x - 1, 0)];
                    float right = elevations[y * resolution + Mathf.Min(x + 1, resolution - 1)];
                    float down = elevations[Mathf.Max(y - 1, 0) * resolution + x];
                    float up = elevations[Mathf.Min(y + 1, resolution - 1) * resolution + x];

                    float dx = (right - left) / (2f * sampleSpacingMeters);
                    float dz = (up - down) / (2f * sampleSpacingMeters);
                    float slope = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) * 8f);

                    float water = elevation <= 0f ? 1f : 0f;
                    float normalizedElevation = Mathf.Clamp01((elevation - field.MinElevation) / range);
                    // Shoreline band: within ±6 m of sea level.
                    float shore = 1f - Mathf.Clamp01(Mathf.Abs(elevation) / 6f);

                    colors[i] = new Color(water, normalizedElevation, slope, shore);
                }
            }

            var triangles = BuildIndices(resolution);

            var mesh = new Mesh { name = name };
            // 128^2 = 16 384 vertices fits in 16-bit indices, but higher resolutions
            // do not, and a silently corrupt mesh is worse than the extra bandwidth.
            mesh.indexFormat = vertexCount > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors32 = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: false);

            return mesh;
        }

        static int[] BuildIndices(int resolution)
        {
            int quads = (resolution - 1) * (resolution - 1);
            var triangles = new int[quads * 6];
            int t = 0;

            for (int y = 0; y < resolution - 1; y++)
            {
                for (int x = 0; x < resolution - 1; x++)
                {
                    int bl = y * resolution + x;
                    int br = bl + 1;
                    int tl = bl + resolution;
                    int tr = tl + 1;

                    // Clockwise when viewed from +Y, which is Unity's front face for
                    // an up-facing surface.
                    triangles[t++] = bl; triangles[t++] = tl; triangles[t++] = br;
                    triangles[t++] = br; triangles[t++] = tl; triangles[t++] = tr;
                }
            }

            return triangles;
        }

        /// <summary>
        /// Builds LOD0/1/2 at full, half and quarter vertex density. The map is only
        /// 2 m across so LOD switching is subtle, but it earns its keep when the user
        /// steps back to take in the whole system.
        /// </summary>
        public static Mesh[] BuildLodChain(TerrainHeightfield field, AppConfig config)
        {
            int lod0 = config.TerrainMeshResolution;
            return new[]
            {
                Build(field, lod0, config, "TerrainMesh_LOD0"),
                Build(field, Mathf.Max(16, lod0 / 2), config, "TerrainMesh_LOD1"),
                Build(field, Mathf.Max(8, lod0 / 4), config, "TerrainMesh_LOD2")
            };
        }
    }
}

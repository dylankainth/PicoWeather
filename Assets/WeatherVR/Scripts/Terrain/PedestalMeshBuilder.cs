using System.Collections.Generic;
using UnityEngine;

namespace WeatherVR.Terrain
{
    /// <summary>
    /// Builds the holographic plinth the map stands on: a flared lip just under the
    /// terrain's edge, tapering down to a narrower base. Pure geometry, no weather
    /// dependency, so it is built once in <see cref="PedestalRenderer"/>'s Awake.
    ///
    /// Everything is in normalised map units: X/Z centred on [-0.5, 0.5] (the same
    /// square <see cref="TerrainMeshBuilder"/> fills), Y = 0 at the map plane where
    /// the terrain sits, negative Y going down toward the floor. The map root's own
    /// scale (<c>MapSizeMeters</c>) turns these into VR metres, same as every other
    /// mesh under the map root.
    /// </summary>
    public static class PedestalMeshBuilder
    {
        /// <summary>
        /// Three stacked rectangular rings, widest at the flared lip:
        ///   Ring A (top)    -- half-extent <paramref name="topHalf"/>, at y = -topInset.
        ///   Ring B (lip)    -- half-extent <paramref name="rimHalf"/>, at y = -topInset - lipDepth.
        ///                      Wider than Ring A: the flare reads as an overhanging
        ///                      lip just below the map's edge.
        ///   Ring C (base)   -- half-extent <paramref name="baseHalf"/>, at y = -totalDepth.
        ///                      Narrower again: the body tapers toward the floor like
        ///                      a museum plinth.
        /// Vertex colour (r channel) is 1 across the lip band (Ring A/B) and fades to
        /// 0 down the tapered body to Ring C -- the pedestal shader reads this as an
        /// emissive glow band.
        /// </summary>
        public static Mesh Build(
            float topHalf = 0.52f,
            float rimHalf = 0.57f,
            float baseHalf = 0.44f,
            float topInset = 0.004f,
            float lipDepth = 0.045f,
            float totalDepth = 0.34f)
        {
            var vertices = new List<Vector3>(32);
            var normals = new List<Vector3>(32);
            var colors = new List<Color32>(32);
            var triangles = new List<int>(96);

            float yTop = -topInset;
            float yLip = -topInset - lipDepth;
            float yBase = -totalDepth;

            var ringA = Ring(topHalf, yTop);
            var ringB = Ring(rimHalf, yLip);
            var ringC = Ring(baseHalf, yBase);

            var glowFull = new Color32(255, 255, 255, 255);
            var glowNone = new Color32(0, 0, 0, 255);

            // Lip band: flares out from Ring A (top) down to Ring B, full glow both ends.
            AddBand(ringA, ringB, glowFull, glowFull, vertices, normals, colors, triangles);
            // Body: tapers from Ring B down to Ring C, glow fades to zero at the base.
            AddBand(ringB, ringC, glowFull, glowNone, vertices, normals, colors, triangles);

            // Top cap faces +Y, visible in the thin margin where the pedestal's lip
            // (topHalf) is wider than the terrain mesh's own edge (0.5).
            AddCap(ringA, glowFull, facingUp: true, vertices, normals, colors, triangles);
            // Base cap faces -Y; cheap to include and avoids an open bottom if anyone
            // ever looks up at the underside.
            AddCap(ringC, glowNone, facingUp: false, vertices, normals, colors, triangles);

            var mesh = new Mesh { name = "PedestalMesh" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: false);
            return mesh;
        }

        /// <summary>Four corners of a square ring, wound counter-clockwise as seen from above.</summary>
        static Vector3[] Ring(float half, float y) => new[]
        {
            new Vector3(-half, y, -half),
            new Vector3( half, y, -half),
            new Vector3( half, y,  half),
            new Vector3(-half, y,  half),
        };

        /// <summary>
        /// One side band between two same-winding rings. Follows the exact
        /// bottom/top-index winding <see cref="BuildingMeshBuilder"/> uses for its
        /// walls -- (bottom_i, top_j, bottom_j) and (bottom_i, top_i, top_j) -- which
        /// depends only on both rings sharing a CCW-from-above winding and "top"
        /// being the higher ring, not on the rings being the same size. That is what
        /// lets the same formula produce a correct outward normal whether the band
        /// flares out (lip) or tapers in (body).
        /// </summary>
        static void AddBand(Vector3[] bottomRing, Vector3[] topRing,
                             Color32 bottomColor, Color32 topColor,
                             List<Vector3> vertices, List<Vector3> normals, List<Color32> colors,
                             List<int> triangles)
        {
            int n = bottomRing.Length;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;

                Vector3 bi = bottomRing[i], bj = bottomRing[j];
                Vector3 ti = topRing[i], tj = topRing[j];

                // Flat normal from the actual (possibly slanted) quad, shared by both
                // triangles of this face.
                Vector3 normal = Vector3.Cross(ti - bi, bj - bi).normalized;

                int v0 = vertices.Count;
                AddVertex(bi, normal, bottomColor, vertices, normals, colors);
                int v1 = vertices.Count;
                AddVertex(tj, normal, topColor, vertices, normals, colors);
                int v2 = vertices.Count;
                AddVertex(bj, normal, bottomColor, vertices, normals, colors);
                int v3 = vertices.Count;
                AddVertex(ti, normal, topColor, vertices, normals, colors);

                triangles.Add(v0); triangles.Add(v1); triangles.Add(v2);
                triangles.Add(v0); triangles.Add(v3); triangles.Add(v1);
            }
        }

        /// <summary>
        /// A centroid-fan cap over one ring. Facing +Y uses the ring order reversed
        /// (mirrors <c>BuildingMeshBuilder</c>'s roof fan); facing -Y uses the ring's
        /// own CCW-from-above order.
        /// </summary>
        static void AddCap(Vector3[] ring, Color32 color, bool facingUp,
                            List<Vector3> vertices, List<Vector3> normals, List<Color32> colors,
                            List<int> triangles)
        {
            Vector3 centroid = Vector3.zero;
            foreach (var p in ring) centroid += p;
            centroid /= ring.Length;

            Vector3 normal = facingUp ? Vector3.up : Vector3.down;

            int centroidIndex = vertices.Count;
            AddVertex(centroid, normal, color, vertices, normals, colors);

            int n = ring.Length;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int pFirst = vertices.Count;
                AddVertex(facingUp ? ring[j] : ring[i], normal, color, vertices, normals, colors);
                int pSecond = vertices.Count;
                AddVertex(facingUp ? ring[i] : ring[j], normal, color, vertices, normals, colors);

                triangles.Add(centroidIndex); triangles.Add(pFirst); triangles.Add(pSecond);
            }
        }

        static void AddVertex(Vector3 position, Vector3 normal, Color32 color,
                               List<Vector3> vertices, List<Vector3> normals, List<Color32> colors)
        {
            vertices.Add(position);
            normals.Add(normal);
            colors.Add(color);
        }
    }
}

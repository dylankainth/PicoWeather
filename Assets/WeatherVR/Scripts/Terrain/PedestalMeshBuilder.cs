using System.Collections.Generic;
using UnityEngine;

namespace WeatherVR.Terrain
{
    /// <summary>
    /// Builds the liquid-glass plinth the map stands on: a chamfered-square profile
    /// (four full-width axis flats + four corner chamfers) stacked through five rings
    /// from just under the table down to the floor. Pure geometry, no weather
    /// dependency, so it is built once in <see cref="PedestalRenderer"/>'s Awake.
    ///
    /// Everything is in normalised map units: X/Z centred on [-0.5, 0.5] (the same
    /// square <see cref="TerrainMeshBuilder"/> fills), Y = 0 at the map plane where
    /// the terrain sits, negative Y going down toward the floor. The map root's own
    /// scale (<c>MapSizeMeters</c>) turns these into VR metres, same as every other
    /// mesh under the map root.
    ///
    /// Why a chamfered square and not a regular polygon: the terrain is a square, so
    /// its corners sit at radius sqrt(0.5^2 + 0.5^2) = 0.707. A hexagon needs an
    /// inradius that large to cover them everywhere, i.e. a circumradius of ~0.83 --
    /// a 3.3 m plinth under a 2 m map, and it would force the carousel's four-wall
    /// snap (<see cref="WeatherVR.UI.Carousel.WeatherCarouselFollower"/>) to become a
    /// six-wall one. A chamfered square keeps the four full-width axis flats the
    /// carousel already mounts against untouched, only cutting material at the
    /// corners, and each ring's chamfer is sized so the flat run still exceeds the
    /// terrain's own half-extent (see the per-ring comments below) -- the corners
    /// stay covered.
    ///
    /// That last guarantee holds only at <c>unitScale == 1</c>, i.e. while the map is at
    /// <see cref="WeatherVR.Core.AppConfig.PedestalReferenceMapSizeMeters"/>. The plinth
    /// is deliberately held at a fixed size in VR metres as the map grows (see that
    /// field), so on a 3 m map the crown sits at 0.570 * 2/3 = 0.380 map units while the
    /// terrain still reaches 0.500 -- the map overhangs its plinth, and the heightfield's
    /// underside is no longer covered by the shader's Cull Front depth pass. Intended,
    /// and the reason the ring radii below are not simply re-authored: they stay the
    /// numbers that were tuned against a 2 m map, and one scalar records the departure.
    /// </summary>
    public static class PedestalMeshBuilder
    {
        /// <summary>One ring's shape: half-extent (to the flat faces) and chamfer depth.</summary>
        struct RingSpec
        {
            public float Half;
            public float Chamfer;
            public float Y;
            public RingSpec(float half, float chamfer, float y) { Half = half; Chamfer = chamfer; Y = y; }
        }

        const int BandCount = 4;

        /// <summary>
        /// Five stacked chamfered-square rings, widest at the crown just under the
        /// map's edge, narrowing to a base on the floor:
        ///   R0 table    -- half 0.520, right at the underside of the map.
        ///   R1 crown    -- half 0.570, the widest ring; the flare from R0 to R1 reads
        ///                  as an overhanging lip just below the map's edge.
        ///   R2 girdle   -- half 0.552, narrowing back in.
        ///   R3 pavilion -- half 0.500, narrower again.
        ///   R4 base     -- half 0.440, tapers to the floor like a museum plinth.
        /// The chamfer *grows* from crown to base (0.100 -> 0.160) so no two of the
        /// four side bands share a facet slope -- 4 bands x 8 facets = 32 distinct
        /// flat normals, which is what reads as cut crystal rather than a smooth cone.
        ///
        /// Vertex colour carries four independent signals, all consumed by
        /// Pedestal.shader:
        ///   .r -- lip glow mask: 1 across R0/R1, fading to 0 by R4.
        ///   .g -- per-facet hash (0..1 across the 8 facets, shared by all 4 verts of
        ///         a facet's quad) -- used for the kaleidoscope phase and per-facet
        ///         tick variation.
        ///   .b -- band index, normalised 0 (crown) .. 1 (base) -- used to vary the
        ///         coordinate-ladder density with depth.
        ///   .a -- 1 on the side bands, 0 on the top/base caps -- switches the
        ///         coordinate motif between the Y-axis ladder (sides) and the XZ
        ///         graticule (caps) with a single lerp instead of a normal test.
        /// </summary>
        /// <param name="unitScale">
        /// Uniform multiplier on every ring radius, chamfer and height, in map units.
        /// Pass <see cref="WeatherVR.Core.AppConfig.PedestalMapUnitScale"/> to hold the
        /// plinth at a constant size in VR metres while the map root's scale changes; 1
        /// lets it scale with the map like every other mesh under the root.
        /// </param>
        public static Mesh Build(float unitScale = 1f)
        {
            var vertices = new List<Vector3>(192);
            var normals = new List<Vector3>(192);
            var colors = new List<Color32>(192);
            var triangles = new List<int>(576);

            float s = Mathf.Max(unitScale, 1e-3f);

            // Radii/heights as authored against a 2 m map, uniformly scaled. Scaling all
            // three components by the same factor is what keeps every facet slope -- and
            // so all 32 flat normals the shader relies on -- identical to the authored
            // shape rather than squashing it.
            var r0 = new RingSpec(0.520f * s, 0.090f * s, -0.004f * s); // table
            var r1 = new RingSpec(0.570f * s, 0.100f * s, -0.049f * s); // crown
            var r2 = new RingSpec(0.552f * s, 0.130f * s, -0.120f * s); // girdle
            var r3 = new RingSpec(0.500f * s, 0.160f * s, -0.235f * s); // pavilion
            var r4 = new RingSpec(0.440f * s, 0.120f * s, -0.340f * s); // base

            var ring0 = Ring(r0);
            var ring1 = Ring(r1);
            var ring2 = Ring(r2);
            var ring3 = Ring(r3);
            var ring4 = Ring(r4);

            // Glow band: full strength across the crown flare, fading to nothing by
            // the base -- same visual role as the original two-ring lip, generalised
            // across five rings.
            const byte glowFull = 255;
            const byte glowMid1 = 170;
            const byte glowMid2 = 85;
            const byte glowNone = 0;

            AddBand(ring0, ring1, glowFull, glowFull, 0, BandCount, vertices, normals, colors, triangles);
            AddBand(ring1, ring2, glowFull, glowMid1, 1, BandCount, vertices, normals, colors, triangles);
            AddBand(ring2, ring3, glowMid1, glowMid2, 2, BandCount, vertices, normals, colors, triangles);
            AddBand(ring3, ring4, glowMid2, glowNone, 3, BandCount, vertices, normals, colors, triangles);

            // Top cap faces +Y, visible in the thin margin where the pedestal's crown
            // is wider than the terrain mesh's own edge.
            AddCap(ring0, glowFull, facingUp: true, vertices, normals, colors, triangles);
            // Base cap faces -Y; cheap to include and avoids an open bottom if anyone
            // ever looks up at the underside.
            AddCap(ring4, glowNone, facingUp: false, vertices, normals, colors, triangles);

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

        /// <summary>
        /// Eight corners of a chamfered square, wound counter-clockwise as seen from
        /// above: four full-width flats on the +-X/+-Z axes (what the carousel's
        /// wall-mount snaps against) and four corner chamfers between them.
        /// </summary>
        static Vector3[] Ring(RingSpec spec)
        {
            float h = spec.Half;
            float c = Mathf.Min(spec.Chamfer, h * 0.9f);
            float y = spec.Y;
            return new[]
            {
                new Vector3(-h + c, y, -h),
                new Vector3( h - c, y, -h),
                new Vector3( h,     y, -h + c),
                new Vector3( h,     y,  h - c),
                new Vector3( h - c, y,  h),
                new Vector3(-h + c, y,  h),
                new Vector3(-h,     y,  h - c),
                new Vector3(-h,     y, -h + c),
            };
        }

        /// <summary>
        /// One side band between two same-winding rings. Follows the exact
        /// bottom/top-index winding <see cref="BuildingMeshBuilder"/> uses for its
        /// walls -- (bottom_i, top_j, bottom_j) and (bottom_i, top_i, top_j) -- which
        /// depends only on both rings sharing a CCW-from-above winding and "top"
        /// being the higher ring, not on the rings being the same size or shape. That
        /// is what lets the same formula produce a correct outward normal whether the
        /// band flares out (crown) or tapers in (base), and whether the ring is a
        /// square or a chamfered one.
        /// </summary>
        static void AddBand(Vector3[] bottomRing, Vector3[] topRing,
                             byte glowBottom, byte glowTop, int bandIndex, int bandCount,
                             List<Vector3> vertices, List<Vector3> normals, List<Color32> colors,
                             List<int> triangles)
        {
            int n = bottomRing.Length;
            byte bandByte = (byte)(bandIndex * 255 / Mathf.Max(1, bandCount - 1));

            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;

                Vector3 bi = bottomRing[i], bj = bottomRing[j];
                Vector3 ti = topRing[i], tj = topRing[j];

                // Flat normal from the actual (possibly slanted) quad, shared by both
                // triangles of this facet -- this is what gives each of the 32 bands
                // its own distinct facet normal.
                Vector3 normal = Vector3.Cross(ti - bi, bj - bi).normalized;

                // One facet hash per quad (not per vertex), shared by all four corners
                // of this facet so the kaleidoscope phase and tick variation read as
                // per-facet rather than per-vertex noise.
                byte facetHash = (byte)((i + 0.5f) / n * 255f);

                var colorBottom = new Color32(glowBottom, facetHash, bandByte, 255);
                var colorTop = new Color32(glowTop, facetHash, bandByte, 255);

                int v0 = vertices.Count;
                AddVertex(bi, normal, colorBottom, vertices, normals, colors);
                int v1 = vertices.Count;
                AddVertex(tj, normal, colorTop, vertices, normals, colors);
                int v2 = vertices.Count;
                AddVertex(bj, normal, colorBottom, vertices, normals, colors);
                int v3 = vertices.Count;
                AddVertex(ti, normal, colorTop, vertices, normals, colors);

                triangles.Add(v0); triangles.Add(v1); triangles.Add(v2);
                triangles.Add(v0); triangles.Add(v3); triangles.Add(v1);
            }
        }

        /// <summary>
        /// A centroid-fan cap over one ring. Facing +Y uses the ring order reversed
        /// (mirrors <c>BuildingMeshBuilder</c>'s roof fan); facing -Y uses the ring's
        /// own CCW-from-above order. Alpha is written 0 so the shader reads caps as
        /// the XZ graticule rather than the side bands' Y-axis ladder.
        /// </summary>
        static void AddCap(Vector3[] ring, byte glow, bool facingUp,
                            List<Vector3> vertices, List<Vector3> normals, List<Color32> colors,
                            List<int> triangles)
        {
            Vector3 centroid = Vector3.zero;
            foreach (var p in ring) centroid += p;
            centroid /= ring.Length;

            Vector3 normal = facingUp ? Vector3.up : Vector3.down;
            var color = new Color32(glow, 0, (byte)(facingUp ? 0 : 255), 0);

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

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WeatherVR.IntroEarth
{
    /// <summary>
    /// Creates a tiny, texture-free globe at runtime.
    ///
    /// The ocean and the rough continent mask share one 320-triangle icosphere.
    /// There are no bitmap textures, normal maps, downloads or high-detail meshes,
    /// so the complete intro costs only a few small meshes and three materials.
    /// </summary>
    public static class LowPolyEarthBuilder
    {
        public const float Radius = 0.34f;
        public const float LondonLatitude = 51.5074f;
        public const float LondonLongitude = -0.1278f;
        static readonly Color OceanColour = new Color(0.045f, 0.24f, 0.43f, 1f);
        static readonly Color LandColour = new Color(0.16f, 0.64f, 0.48f, 1f);

        static readonly Vector3[] BaseVertices =
        {
            new Vector3(-1,  1.618034f, 0), new Vector3( 1,  1.618034f, 0),
            new Vector3(-1, -1.618034f, 0), new Vector3( 1, -1.618034f, 0),
            new Vector3(0, -1,  1.618034f), new Vector3(0,  1,  1.618034f),
            new Vector3(0, -1, -1.618034f), new Vector3(0,  1, -1.618034f),
            new Vector3( 1.618034f, 0, -1), new Vector3( 1.618034f, 0,  1),
            new Vector3(-1.618034f, 0, -1), new Vector3(-1.618034f, 0,  1)
        };

        static readonly int[] BaseTriangles =
        {
             0,11, 5,  0, 5, 1,  0, 1, 7,  0, 7,10,  0,10,11,
             1, 5, 9,  5,11, 4, 11,10, 2, 10, 7, 6,  7, 1, 8,
             3, 9, 4,  3, 4, 2,  3, 2, 6,  3, 6, 8,  3, 8, 9,
             4, 9, 5,  2, 4,11,  6, 2,10,  8, 6, 7,  9, 8, 1
        };

        public sealed class Visual
        {
            public GameObject Root;
            public Transform LondonMarker;
            public Renderer[] Renderers;
            public Mesh[] Meshes;
        }

        /// <summary>Builds the complete Earth visual below <paramref name="parent"/>.</summary>
        public static Visual Build(
            Transform parent,
            Material earthMaterial,
            Material gridMaterial,
            Material markerMaterial)
        {
            var directions = new List<Vector3>(BaseVertices.Length);
            foreach (Vector3 vertex in BaseVertices)
                directions.Add(vertex.normalized);

            var triangles = new List<int>(BaseTriangles);
            Subdivide(directions, triangles);
            Subdivide(directions, triangles);

            var root = new GameObject("Low Detail Earth");
            root.transform.SetParent(parent, false);

            Mesh earthMesh = BuildFacetedMesh(
                "Intro Earth Surface",
                directions,
                triangles,
                Radius);
            Renderer earth = CreateMeshObject(
                "Earth Surface — 320 triangles",
                root.transform,
                earthMesh,
                earthMaterial);

            Mesh gridMesh = BuildCoordinateGrid("Earth Coordinate Mesh", Radius * 1.017f);
            Renderer grid = CreateMeshObject(
                "Coordinate Mesh — 448 vertices",
                root.transform,
                gridMesh,
                gridMaterial);

            Mesh markerMesh = BuildOctahedron("London Marker");
            Renderer marker = CreateMeshObject(
                "London",
                root.transform,
                markerMesh,
                markerMaterial);

            Vector3 londonDirection = DirectionFromLatitudeLongitude(
                LondonLatitude,
                LondonLongitude);
            marker.transform.localPosition = londonDirection * (Radius * 1.055f);
            marker.transform.localRotation =
                Quaternion.FromToRotation(Vector3.up, londonDirection);
            marker.transform.localScale = Vector3.one * 0.025f;

            earth.sharedMaterial.renderQueue = 3000;
            grid.sharedMaterial.renderQueue = 3001;
            marker.sharedMaterial.renderQueue = 3002;

            return new Visual
            {
                Root = root,
                LondonMarker = marker.transform,
                Renderers = new[] { earth, grid, marker },
                Meshes = new[] { earthMesh, gridMesh, markerMesh }
            };
        }

        /// <summary>Returns the outward unit direction for a geographic coordinate.</summary>
        public static Vector3 DirectionFromLatitudeLongitude(float latitude, float longitude)
        {
            float lat = latitude * Mathf.Deg2Rad;
            float lon = longitude * Mathf.Deg2Rad;
            float cosLat = Mathf.Cos(lat);
            return new Vector3(
                cosLat * Mathf.Sin(lon),
                Mathf.Sin(lat),
                cosLat * Mathf.Cos(lon));
        }

        static Renderer CreateMeshObject(
            string name,
            Transform parent,
            Mesh mesh,
            Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return renderer;
        }

        static void Subdivide(List<Vector3> vertices, List<int> triangles)
        {
            var midpoints = new Dictionary<long, int>();
            var next = new List<int>(triangles.Count * 4);

            for (int i = 0; i < triangles.Count; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                int ab = Midpoint(a, b, vertices, midpoints);
                int bc = Midpoint(b, c, vertices, midpoints);
                int ca = Midpoint(c, a, vertices, midpoints);

                next.Add(a);  next.Add(ab); next.Add(ca);
                next.Add(b);  next.Add(bc); next.Add(ab);
                next.Add(c);  next.Add(ca); next.Add(bc);
                next.Add(ab); next.Add(bc); next.Add(ca);
            }

            triangles.Clear();
            triangles.AddRange(next);
        }

        static int Midpoint(
            int a,
            int b,
            List<Vector3> vertices,
            Dictionary<long, int> cache)
        {
            int low = Mathf.Min(a, b);
            int high = Mathf.Max(a, b);
            long key = ((long)low << 32) | (uint)high;
            if (cache.TryGetValue(key, out int existing))
                return existing;

            int index = vertices.Count;
            vertices.Add((vertices[a] + vertices[b]).normalized);
            cache.Add(key, index);
            return index;
        }

        static Mesh BuildFacetedMesh(
            string name,
            List<Vector3> directions,
            List<int> sourceTriangles,
            float radius)
        {
            var vertices = new List<Vector3>(sourceTriangles.Count);
            var normals = new List<Vector3>(sourceTriangles.Count);
            var colours = new List<Color>(sourceTriangles.Count);
            var triangles = new List<int>(sourceTriangles.Count);

            for (int i = 0; i < sourceTriangles.Count; i += 3)
            {
                Vector3 a = directions[sourceTriangles[i]];
                Vector3 b = directions[sourceTriangles[i + 1]];
                Vector3 c = directions[sourceTriangles[i + 2]];

                int first = vertices.Count;
                Vector3 faceNormal = Vector3.Cross(b - a, c - a).normalized;
                if (Vector3.Dot(faceNormal, a + b + c) < 0f)
                    faceNormal = -faceNormal;
                Color colour = IsLand((a + b + c).normalized)
                    ? LandColour
                    : OceanColour;

                vertices.Add(a * radius);
                vertices.Add(b * radius);
                vertices.Add(c * radius);
                normals.Add(faceNormal);
                normals.Add(faceNormal);
                normals.Add(faceNormal);
                colours.Add(colour);
                colours.Add(colour);
                colours.Add(colour);
                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0, true);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: true);
            return mesh;
        }

        // THESE BROAD ELLIPSES ARE THE COMPLETE "EARTH DATA".
        // THEY SUGGEST THE CONTINENTS WITHOUT A TEXTURE, MAP FILE OR API REQUEST.
        static bool IsLand(Vector3 direction)
        {
            float latitude = Mathf.Asin(direction.y) * Mathf.Rad2Deg;
            float longitude = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;

            return InEllipse(latitude, longitude,  46f, -105f, 32f, 50f) || // North America
                   InEllipse(latitude, longitude, -17f,  -61f, 39f, 20f) || // South America
                   InEllipse(latitude, longitude,  53f,   14f, 16f, 24f) || // Europe
                   InEllipse(latitude, longitude,   7f,   20f, 36f, 27f) || // Africa
                   InEllipse(latitude, longitude,  44f,   85f, 29f, 66f) || // Asia
                   InEllipse(latitude, longitude, -25f,  135f, 17f, 24f) || // Australia
                   InEllipse(latitude, longitude,  73f,  -41f, 12f, 17f) || // Greenland
                   latitude < -70f;                                         // Antarctica
        }

        static bool InEllipse(
            float latitude,
            float longitude,
            float centreLatitude,
            float centreLongitude,
            float latitudeRadius,
            float longitudeRadius)
        {
            float lonDelta = Mathf.DeltaAngle(centreLongitude, longitude);
            float x = lonDelta / longitudeRadius;
            float y = (latitude - centreLatitude) / latitudeRadius;
            return x * x + y * y <= 1f;
        }

        static Mesh BuildOctahedron(string name)
        {
            Vector3[] vertices =
            {
                Vector3.up,
                Vector3.down,
                Vector3.left,
                Vector3.right,
                Vector3.forward,
                Vector3.back
            };
            int[] triangles =
            {
                0,4,3, 0,2,4, 0,5,2, 0,3,5,
                1,3,4, 1,4,2, 1,2,5, 1,5,3
            };
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.colors = WhiteColours(vertices.Length);
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: true);
            return mesh;
        }

        static Mesh BuildCoordinateGrid(string name, float radius)
        {
            const int Segments = 32;
            var vertices = new List<Vector3>(448);
            var indices = new List<int>(448);

            // Three latitude rings.
            foreach (float latitude in new[] { -45f, 0f, 45f })
            {
                for (int i = 0; i < Segments; i++)
                {
                    float lonA = i * (360f / Segments) - 180f;
                    float lonB = (i + 1) * (360f / Segments) - 180f;
                    AddLine(
                        DirectionFromLatitudeLongitude(latitude, lonA) * radius,
                        DirectionFromLatitudeLongitude(latitude, lonB) * radius,
                        vertices,
                        indices);
                }
            }

            // Four great-circle meridians. Each is a complete circle.
            foreach (float longitude in new[] { 0f, 45f, 90f, 135f })
            {
                for (int i = 0; i < Segments; i++)
                {
                    float angleA = i * (360f / Segments) - 180f;
                    float angleB = (i + 1) * (360f / Segments) - 180f;
                    AddLine(
                        DirectionFromLatitudeLongitude(angleA, longitude) * radius,
                        DirectionFromLatitudeLongitude(angleB, longitude) * radius,
                        vertices,
                        indices);
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.colors = WhiteColours(vertices.Count);
            mesh.SetIndices(indices, MeshTopology.Lines, 0, true);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(markNoLongerReadable: true);
            return mesh;
        }

        static void AddLine(
            Vector3 a,
            Vector3 b,
            List<Vector3> vertices,
            List<int> indices)
        {
            int first = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            indices.Add(first);
            indices.Add(first + 1);
        }

        static Color[] WhiteColours(int count)
        {
            var colours = new Color[count];
            for (int i = 0; i < count; i++)
                colours[i] = Color.white;
            return colours;
        }
    }
}

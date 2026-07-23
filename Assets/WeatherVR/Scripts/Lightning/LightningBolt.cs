using System.Collections.Generic;
using UnityEngine;

namespace WeatherVR.Lightning
{
    /// <summary>
    /// One lightning channel: a procedurally generated branching arc plus the point
    /// light that makes the rest of the scene respond to it.
    ///
    /// The geometry is built as pairs of perpendicular ribbons rather than
    /// camera-facing billboards. Billboards are cheaper, but in stereo they have to
    /// be solved per eye, and a bolt only a few centimetres from the viewer's face
    /// is exactly where that breaks down. Cross-ribbons look correct from every
    /// angle and from both eyes with no per-camera work at all.
    ///
    /// Bolts are pooled: <see cref="Strike"/> rebuilds and replays an existing
    /// instance rather than allocating.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LightningBolt : MonoBehaviour
    {
        [Header("Channel shape")]
        [Tooltip("Subdivision passes. Each doubles the segment count, so 5 gives 32 segments.")]
        [Range(2, 6)] public int Subdivisions = 5;

        [Tooltip("Lateral displacement of the first subdivision, as a fraction of channel length.")]
        [Range(0f, 0.5f)] public float Jaggedness = 0.14f;

        [Tooltip("Chance a given segment sprouts a branch.")]
        [Range(0f, 1f)] public float BranchProbability = 0.28f;

        [Tooltip("Maximum branches per bolt.")]
        [Range(0, 12)] public int MaxBranches = 6;

        [Header("Appearance")]
        [Tooltip("Channel half-width in map-local units at the top of the bolt.")]
        public float Width = 0.006f;

        [Tooltip("Peak intensity of the accompanying point light.")]
        public float LightIntensity = 8f;

        [Tooltip("Range of the point light, in world metres.")]
        public float LightRange = 3.5f;

        public Color BoltColor = new Color(0.82f, 0.88f, 1f);

        [Header("Timing")]
        [Tooltip("Total visible lifetime of the flash, seconds.")]
        public float Lifetime = 0.32f;

        [Tooltip("Return strokes. Real flashes are 3-4 strokes ~50 ms apart, which is " +
                 "what produces the characteristic stutter.")]
        [Range(1, 6)] public int ReturnStrokes = 3;

        static readonly int ColorId = Shader.PropertyToID("_BoltColor");
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        MaterialPropertyBlock _propertyBlock;
        Light _light;
        Mesh _mesh;

        float _elapsed;
        float _currentIntensity;

        /// <summary>True while the bolt is visible and animating.</summary>
        public bool IsActive { get; private set; }

        /// <summary>World position of the strike's ground termination.</summary>
        public Vector3 GroundPointWorld { get; private set; }

        /// <summary>0..1 brightness this frame, for the cloud shader and the ambient flash.</summary>
        public float CurrentIntensity => _currentIntensity;

        void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();
            _propertyBlock = new MaterialPropertyBlock();

            _mesh = new Mesh { name = "LightningChannel" };
            _mesh.MarkDynamic();
            _meshFilter.sharedMesh = _mesh;

            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _light = GetComponentInChildren<Light>();
            if (_light == null)
            {
                var lightObject = new GameObject("BoltLight");
                lightObject.transform.SetParent(transform, worldPositionStays: false);
                _light = lightObject.AddComponent<Light>();
            }
            _light.type = LightType.Point;
            _light.shadows = LightShadows.None;
            _light.renderMode = LightRenderMode.ForcePixel;
            _light.color = BoltColor;

            Deactivate();
        }

        /// <summary>
        /// Rebuilds the channel between two points in the parent's local space and
        /// starts the flash.
        /// </summary>
        public void Strike(Vector3 topLocal, Vector3 groundLocal, int seed)
        {
            var random = new System.Random(seed);

            var channel = BuildChannel(topLocal, groundLocal, random);
            var branches = BuildBranches(channel, random);

            RebuildMesh(channel, branches);

            // The light sits partway down the channel: putting it at the ground point
            // under-lights the cloud base, and at the top it under-lights the terrain.
            Vector3 lightLocal = Vector3.Lerp(groundLocal, topLocal, 0.35f);
            _light.transform.localPosition = lightLocal;
            _light.range = LightRange;
            _light.color = BoltColor;

            GroundPointWorld = transform.TransformPoint(groundLocal);

            _elapsed = 0f;
            IsActive = true;
            gameObject.SetActive(true);
            _meshRenderer.enabled = true;
            _light.enabled = true;
        }

        void Update()
        {
            if (!IsActive) return;

            _elapsed += Time.deltaTime;
            if (_elapsed >= Lifetime)
            {
                Deactivate();
                return;
            }

            _currentIntensity = Envelope(_elapsed / Lifetime);

            _propertyBlock.SetColor(ColorId, BoltColor);
            _propertyBlock.SetFloat(IntensityId, _currentIntensity);
            _meshRenderer.SetPropertyBlock(_propertyBlock);

            _light.intensity = _currentIntensity * LightIntensity;
        }

        /// <summary>
        /// Brightness over the flash. A single decay looks like a camera flash; the
        /// stroke train is what makes it read as lightning.
        /// </summary>
        float Envelope(float t)
        {
            float value = 0f;
            for (int i = 0; i < ReturnStrokes; i++)
            {
                float strokeStart = i / (float)ReturnStrokes * 0.62f;
                float dt = t - strokeStart;
                if (dt < 0f) continue;
                // Later strokes are dimmer than the first.
                float gain = Mathf.Pow(0.62f, i);
                value = Mathf.Max(value, gain * Mathf.Exp(-dt * 26f) * (1f - Mathf.Exp(-dt * 700f)));
            }
            // Overall fade so nothing survives past the lifetime.
            return Mathf.Clamp01(value) * (1f - Mathf.SmoothStep(0.75f, 1f, t));
        }

        public void Deactivate()
        {
            IsActive = false;
            _currentIntensity = 0f;
            if (_meshRenderer != null) _meshRenderer.enabled = false;
            if (_light != null)
            {
                _light.enabled = false;
                _light.intensity = 0f;
            }
        }

        // ------------------------------------------------------- channel shape

        /// <summary>
        /// Midpoint displacement between the two endpoints. Displacement halves with
        /// each pass, which is what gives a lightning channel its self-similar look.
        /// </summary>
        List<Vector3> BuildChannel(Vector3 top, Vector3 ground, System.Random random)
        {
            var points = new List<Vector3> { top, ground };
            float length = Vector3.Distance(top, ground);
            float displacement = length * Jaggedness;

            var boltInstance = new List<Vector3>();
            for (int pass = 0; pass < Subdivisions; pass++)
            {
                boltInstance.Clear();
                for (int i = 0; i < points.Count - 1; i++)
                {
                    Vector3 a = points[i];
                    Vector3 b = points[i + 1];
                    Vector3 mid = (a + b) * 0.5f;

                    // Displace perpendicular to the segment, not in an arbitrary
                    // direction, or the channel doubles back on itself.
                    Vector3 axis = (b - a).normalized;
                    Vector3 perpendicular = Vector3.Cross(axis, RandomUnit(random)).normalized;
                    if (perpendicular.sqrMagnitude < 1e-6f) perpendicular = Vector3.right;

                    mid += perpendicular * displacement * ((float)random.NextDouble() * 2f - 1f);

                    boltInstance.Add(a);
                    boltInstance.Add(mid);
                }
                boltInstance.Add(points[points.Count - 1]);

                points = new List<Vector3>(boltInstance);
                displacement *= 0.5f;
            }

            return points;
        }

        List<List<Vector3>> BuildBranches(List<Vector3> channel, System.Random random)
        {
            var branches = new List<List<Vector3>>();
            if (MaxBranches <= 0 || channel.Count < 4) return branches;

            for (int i = 1; i < channel.Count - 1 && branches.Count < MaxBranches; i++)
            {
                if (random.NextDouble() > BranchProbability) continue;

                Vector3 origin = channel[i];
                Vector3 mainDirection = (channel[i + 1] - channel[i - 1]).normalized;

                // Branches fork downward and outward, and get shorter the further down
                // the channel they leave from — the same taper a real flash shows.
                float remaining = 1f - i / (float)channel.Count;
                float length = Vector3.Distance(channel[0], channel[channel.Count - 1])
                             * Mathf.Lerp(0.08f, 0.32f, remaining) * (0.5f + (float)random.NextDouble());

                Vector3 spread = RandomUnit(random);
                Vector3 direction = (mainDirection + spread * 0.9f).normalized;

                var branch = BuildChannel(origin, origin + direction * length, random);
                branches.Add(branch);
            }

            return branches;
        }

        static Vector3 RandomUnit(System.Random random)
        {
            float z = (float)(random.NextDouble() * 2.0 - 1.0);
            float theta = (float)(random.NextDouble() * System.Math.PI * 2.0);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(theta), z, r * Mathf.Sin(theta));
        }

        // -------------------------------------------------------- mesh assembly

        static readonly List<Vector3> Vertices = new List<Vector3>(2048);
        static readonly List<Vector2> Uvs = new List<Vector2>(2048);
        static readonly List<int> Triangles = new List<int>(4096);

        void RebuildMesh(List<Vector3> channel, List<List<Vector3>> branches)
        {
            Vertices.Clear();
            Uvs.Clear();
            Triangles.Clear();

            AppendRibbons(channel, Width, 1f);
            foreach (var branch in branches)
                AppendRibbons(branch, Width * 0.45f, 0.6f);

            _mesh.Clear();
            _mesh.SetVertices(Vertices);
            _mesh.SetUVs(0, Uvs);
            _mesh.SetTriangles(Triangles, 0);
            _mesh.RecalculateBounds();
        }

        /// <summary>
        /// Appends two perpendicular ribbons along a polyline. UV.y runs 0 at the top
        /// of the channel to 1 at the tip so the shader can taper and flicker along
        /// the length; UV.x runs across the ribbon for the soft-edged core.
        /// </summary>
        static void AppendRibbons(List<Vector3> points, float halfWidth, float brightness)
        {
            if (points == null || points.Count < 2) return;

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < points.Count - 1; i++)
                {
                    Vector3 a = points[i];
                    Vector3 b = points[i + 1];
                    Vector3 axis = b - a;
                    if (axis.sqrMagnitude < 1e-10f) continue;
                    axis.Normalize();

                    // Two ribbon planes at 90 degrees to each other around the channel.
                    Vector3 reference = Mathf.Abs(axis.y) > 0.95f ? Vector3.right : Vector3.up;
                    Vector3 side = Vector3.Cross(axis, reference).normalized;
                    if (pass == 1) side = Vector3.Cross(axis, side).normalized;

                    // Taper towards the tip.
                    float t0 = i / (float)(points.Count - 1);
                    float t1 = (i + 1) / (float)(points.Count - 1);
                    float w0 = halfWidth * Mathf.Lerp(1f, 0.25f, t0);
                    float w1 = halfWidth * Mathf.Lerp(1f, 0.25f, t1);

                    int baseIndex = Vertices.Count;

                    Vertices.Add(a - side * w0);
                    Vertices.Add(a + side * w0);
                    Vertices.Add(b - side * w1);
                    Vertices.Add(b + side * w1);

                    Uvs.Add(new Vector2(0f, t0 * brightness));
                    Uvs.Add(new Vector2(1f, t0 * brightness));
                    Uvs.Add(new Vector2(0f, t1 * brightness));
                    Uvs.Add(new Vector2(1f, t1 * brightness));

                    Triangles.Add(baseIndex + 0);
                    Triangles.Add(baseIndex + 2);
                    Triangles.Add(baseIndex + 1);
                    Triangles.Add(baseIndex + 1);
                    Triangles.Add(baseIndex + 2);
                    Triangles.Add(baseIndex + 3);
                }
            }
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }
    }
}

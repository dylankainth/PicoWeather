using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Clouds
{
    /// <summary>
    /// Owns the transparent box that the volumetric shader marches through, builds
    /// its density textures from a snapshot, and drives its per-frame uniforms.
    ///
    /// The box is a plain unit cube scaled to the map footprint and the atmosphere
    /// column, so <c>VolumetricClouds.shader</c> can do all its work in the cube's
    /// own [-0.5, 0.5] object space.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class CloudRenderer : MonoBehaviour
    {
        [Tooltip("Resolution of the tiling detail-noise volume. 32 is a good trade: " +
                 "64 costs seconds of CPU at load and is not visibly better at " +
                 "tabletop scale.")]
        [Range(16, 64)] public int DetailResolution = 32;

        [Tooltip("How fast the detail noise scrolls, as a multiple of the real wind " +
                 "speed. 1 is true speed, which at 1:25000 is imperceptible.")]
        public float WindTimeScale = 900f;

        // Shader property IDs, resolved once.
        static readonly int DensityVolumeId = Shader.PropertyToID("_DensityVolume");
        static readonly int DetailNoiseId = Shader.PropertyToID("_DetailNoise");
        static readonly int WindScrollId = Shader.PropertyToID("_WindScroll");
        static readonly int CloudTimeId = Shader.PropertyToID("_CloudTime");
        static readonly int StepCountId = Shader.PropertyToID("_StepCount");
        static readonly int LightStepsId = Shader.PropertyToID("_LightSteps");

        MeshFilter _meshFilter;
        MeshRenderer _meshRenderer;
        Material _material;
        Texture3D _densityVolume;

        Vector3 _windScroll;
        Vector2 _meanWind;

        public Material Material => _material;

        /// <summary>Set by <see cref="PerfGovernor"/>; clamped inside the shader too.</summary>
        public int MarchSteps { get; private set; } = 48;

        /// <summary>Set by <see cref="PerfGovernor"/>. Zero disables cloud self-shadowing.</summary>
        public int LightSteps { get; private set; } = 3;

        void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();
            _meshRenderer = GetComponent<MeshRenderer>();

            if (_meshFilter.sharedMesh == null)
                _meshFilter.sharedMesh = BuildUnitCube();

            // Transparent, unlit-by-the-pipeline geometry: it does its own lighting.
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        /// <summary>
        /// Rebuilds the volume for a new snapshot and sizes the box to the configured
        /// map footprint and atmosphere column.
        /// </summary>
        public void Apply(WeatherSnapshot snapshot, AppConfig config)
        {
            if (snapshot?.Weather == null || !snapshot.Weather.IsValid)
            {
                Debug.LogWarning("[WeatherVR] CloudRenderer got no usable weather; hiding clouds.");
                gameObject.SetActive(false);
                return;
            }

            gameObject.SetActive(true);

            EnsureMaterial();

            if (_densityVolume != null) Destroy(_densityVolume);
            _densityVolume = CloudVolumeBuilder.BuildDensityVolume(snapshot.Weather, config);
            _material.SetTexture(DensityVolumeId, _densityVolume);

            var detail = CloudVolumeBuilder.GetOrCreateDetailVolume(DetailResolution, config.ProceduralSeed);
            _material.SetTexture(DetailNoiseId, detail);

            MarchSteps = config.CloudMarchSteps;
            LightSteps = Mathf.RoundToInt(_material.GetFloat(LightStepsId));

            // Local scale: X/Z are the map footprint (the parent already carries the
            // map's own scale, so this is in normalised map units), Y is the
            // atmosphere column expressed in the same units.
            float heightInMapUnits = config.AtmosphereHeightVr / Mathf.Max(config.MapSizeMeters, 1e-4f);
            transform.localScale = new Vector3(1f, heightInMapUnits, 1f);
            // Centre the box so its base sits on the map plane.
            transform.localPosition = new Vector3(0f, heightInMapUnits * 0.5f, 0f);

            _meanWind = MeanWind(snapshot.Weather);

            Debug.Log($"[WeatherVR] Cloud volume {config.CloudVolumeXZ}x{config.CloudVolumeY}x{config.CloudVolumeXZ}, " +
                      $"column {config.AtmosphereFloorMeters:F0}-{config.AtmosphereCeilingMeters:F0} m " +
                      $"= {config.AtmosphereHeightVr:F2} m in VR, mean wind {_meanWind.magnitude:F1} m/s.");
        }

        void EnsureMaterial()
        {
            if (_material != null) return;

            var shader = Shader.Find("WeatherVR/VolumetricClouds");
            if (shader == null)
            {
                Debug.LogError("[WeatherVR] VolumetricClouds shader is missing from the build.");
                enabled = false;
                return;
            }

            _material = new Material(shader) { name = "VolumetricClouds (runtime)" };
            _meshRenderer.sharedMaterial = _material;
        }

        void Update()
        {
            if (_material == null) return;

            // Scroll the detail noise with the wind. At 1:25 000 the true advection
            // rate is invisible, so WindTimeScale exaggerates it to something a
            // viewer can perceive within a demo. The underlying direction stays real.
            float dt = Time.deltaTime;
            _windScroll.x += _meanWind.x * dt * WindTimeScale * 1e-5f;
            _windScroll.z += _meanWind.y * dt * WindTimeScale * 1e-5f;
            // A slow vertical drift stops the volume looking like a sliding wallpaper.
            _windScroll.y += dt * 0.004f;

            _material.SetVector(WindScrollId, _windScroll);
            _material.SetFloat(CloudTimeId, Time.time);
            _material.SetFloat(StepCountId, MarchSteps);
            _material.SetFloat(LightStepsId, LightSteps);
        }

        /// <summary>Applies a quality level chosen by <see cref="PerfGovernor"/>.</summary>
        public void SetQuality(int marchSteps, int lightSteps)
        {
            MarchSteps = Mathf.Clamp(marchSteps, 8, 96);
            LightSteps = Mathf.Clamp(lightSteps, 0, 6);
        }

        static Vector2 MeanWind(WeatherDataset weather)
        {
            if (weather?.cells == null || weather.cells.Length == 0) return Vector2.zero;

            double u = 0, v = 0;
            foreach (var cell in weather.cells)
            {
                u += cell.windU;
                v += cell.windV;
            }
            return new Vector2((float)(u / weather.cells.Length), (float)(v / weather.cells.Length));
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_densityVolume != null) Destroy(_densityVolume);
        }

        /// <summary>
        /// A unit cube spanning [-0.5, 0.5]. Built rather than loaded so the scene has
        /// no dependency on a primitive asset, and with inward-consistent winding so
        /// the shader's <c>Cull Front</c> gives us the back faces.
        /// </summary>
        public static Mesh BuildUnitCube()
        {
            var vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
                new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
                new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f)
            };

            var triangles = new[]
            {
                0, 2, 1, 0, 3, 2, // -Z
                5, 6, 4, 4, 6, 7, // +Z
                4, 7, 0, 0, 7, 3, // -X
                1, 2, 5, 5, 2, 6, // +X
                3, 7, 2, 2, 7, 6, // +Y
                0, 1, 4, 4, 1, 5  // -Y
            };

            var mesh = new Mesh { name = "CloudVolumeCube" };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

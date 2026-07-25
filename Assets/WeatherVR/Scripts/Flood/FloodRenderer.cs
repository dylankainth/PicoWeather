using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Flood
{
    /// <summary>
    /// Draws a translucent storm-surge water plane so hills and buildings that
    /// stand above the chosen level visibly poke through. Sits directly under the
    /// map root, alongside <see cref="WeatherVR.Terrain.TerrainRenderer"/> and
    /// <see cref="WeatherVR.Terrain.BuildingRenderer"/>, so it inherits the map's
    /// placement and scale for free.
    ///
    /// The plane's mesh is built once, in the same normalised [-0.5, 0.5] map
    /// square the terrain occupies; only its local Y moves when the surge level
    /// changes, via <see cref="AppConfig.TerrainElevationToMapUnits"/> — the exact
    /// conversion the terrain mesh itself uses (<c>TerrainMeshBuilder</c>), so a
    /// flood level and the terrain it floods always agree on where "sea level" is.
    /// </summary>
    public class FloodRenderer : MonoBehaviour
    {
        static readonly int ColorId = Shader.PropertyToID("_Color");

        [Tooltip("Water tint and translucency (alpha).")]
        public Color WaterColor = new Color(0.18f, 0.42f, 0.62f, 0.55f);

        [Tooltip("Storm-surge presets, metres above the lowest point in this terrain " +
                 "tile. Index 0 is treated as \"off\" — there is nothing to show at +0.")]
        public float[] SurgePresetsMeters = { 0f, 2f, 5f, 10f };

        Material _material;
        Mesh _mesh;
        MeshFilter _filter;
        MeshRenderer _renderer;
        AppConfig _config;
        float _minElevation;
        bool _hasSnapshot;

        /// <summary>Current surge level, metres above sea level. Null while hidden.</summary>
        public float? LevelMeters { get; private set; }

        /// <summary>Lowest elevation sampled in the current snapshot's terrain, metres asl.</summary>
        public float MinElevationMeters => _minElevation;

        /// <summary>
        /// Caches the config and terrain range for a new snapshot and (re)builds the
        /// plane. Does not itself show water — the user opts into a surge level from
        /// the carousel via <see cref="SetSurge"/>.
        /// </summary>
        public void Apply(WeatherSnapshot snapshot, AppConfig config)
        {
            _config = config;
            _hasSnapshot = snapshot?.Terrain != null;
            _minElevation = _hasSnapshot ? snapshot.Terrain.MinElevation : 0f;

            EnsureMaterial();
            EnsureComponents();
            EnsureMesh();

            SetLevelMeters(null);
        }

        /// <summary>
        /// Selects a surge preset by index (clamped). Index 0 (or any preset ≤ 0)
        /// hides the water.
        /// </summary>
        public void SetSurge(int presetIndex)
        {
            if (SurgePresetsMeters == null || SurgePresetsMeters.Length == 0)
                return;

            presetIndex = Mathf.Clamp(presetIndex, 0, SurgePresetsMeters.Length - 1);
            float depth = SurgePresetsMeters[presetIndex];

            SetLevelMeters(depth > 0f ? _minElevation + depth : (float?)null);
        }

        /// <summary>
        /// Raises or hides the water surface directly. Null (or a level at/below the
        /// lowest terrain sample) hides it.
        /// </summary>
        public void SetLevelMeters(float? metersAsl)
        {
            LevelMeters = metersAsl;

            if (_renderer == null) return;

            if (!metersAsl.HasValue || !_hasSnapshot || metersAsl.Value <= _minElevation || _config == null)
            {
                _renderer.enabled = false;
                return;
            }

            _renderer.enabled = true;
            float localY = _config.TerrainElevationToMapUnits(metersAsl.Value);
            transform.localPosition = new Vector3(0f, localY, 0f);
        }

        void EnsureMaterial()
        {
            if (_material != null) return;

            var shader = Shader.Find("WeatherVR/Water");
            if (shader == null)
            {
                Debug.LogError("[WeatherVR] Water shader is missing from the build.");
                return;
            }

            _material = new Material(shader) { name = "Water (runtime)" };
            _material.SetColor(ColorId, WaterColor);
        }

        void EnsureComponents()
        {
            if (_filter == null) _filter = gameObject.GetComponent<MeshFilter>();
            if (_filter == null) _filter = gameObject.AddComponent<MeshFilter>();

            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<MeshRenderer>();

            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        void EnsureMesh()
        {
            if (_mesh != null)
            {
                _filter.sharedMesh = _mesh;
                return;
            }

            // A single flat quad spanning the same normalised square the terrain
            // occupies. Ripple/Fresnel detail is done per-pixel in the shader, so no
            // extra subdivision is needed.
            _mesh = new Mesh { name = "WaterPlane" };
            _mesh.vertices = new[]
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f,  0.5f),
                new Vector3( 0.5f, 0f,  0.5f),
                new Vector3( 0.5f, 0f, -0.5f),
            };
            _mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            _mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(1f, 1f), new Vector2(1f, 0f),
            };
            _mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
            _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _mesh.RecalculateBounds();

            _filter.sharedMesh = _mesh;
        }

        void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }
    }
}

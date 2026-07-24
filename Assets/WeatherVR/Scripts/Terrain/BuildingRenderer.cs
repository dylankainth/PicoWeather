using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Terrain
{
    /// <summary>
    /// Builds and owns the combined buildings mesh. Sits directly under the map
    /// root, alongside <see cref="TerrainRenderer"/>, so it inherits the map's
    /// placement and scale for free.
    /// </summary>
    public class BuildingRenderer : MonoBehaviour
    {
        Material _material;
        Mesh _mesh;
        MeshFilter _filter;
        MeshRenderer _renderer;

        /// <summary>Rebuilds the mesh for a new snapshot.</summary>
        public void Apply(WeatherSnapshot snapshot, AppConfig config)
        {
            if (snapshot?.Buildings == null)
            {
                Debug.LogWarning("[WeatherVR] BuildingRenderer got no building data.");
                return;
            }

            EnsureMaterial();
            EnsureComponents();

            Clear();

            _mesh = BuildingMeshBuilder.Build(
                snapshot.Buildings, snapshot.Terrain, snapshot.Bounds, config,
                config.MaxBuildings, out int built, out int dropped);

            if (_mesh == null)
            {
                Debug.Log("[WeatherVR] No buildings to draw for this snapshot.");
                return;
            }

            _filter.sharedMesh = _mesh;
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;

            if (dropped > 0)
            {
                Debug.LogWarning($"[WeatherVR] Dropped {dropped} building(s) over the " +
                                 $"{config.MaxBuildings} MaxBuildings budget.");
            }

            int triangles = _mesh.triangles.Length / 3;
            Debug.Log($"[WeatherVR] Buildings built: {built} building(s), " +
                      $"{triangles:N0} triangles, {snapshot.BuildingsSource}.");
        }

        void EnsureMaterial()
        {
            if (_material != null) return;

            var shader = Shader.Find("WeatherVR/Buildings");
            if (shader == null)
            {
                Debug.LogError("[WeatherVR] Buildings shader is missing from the build.");
                return;
            }

            _material = new Material(shader) { name = "Buildings (runtime)" };
        }

        void EnsureComponents()
        {
            if (_filter == null) _filter = gameObject.GetComponent<MeshFilter>();
            if (_filter == null) _filter = gameObject.AddComponent<MeshFilter>();

            if (_renderer == null) _renderer = gameObject.GetComponent<MeshRenderer>();
            if (_renderer == null) _renderer = gameObject.AddComponent<MeshRenderer>();
        }

        void Clear()
        {
            if (_mesh != null)
            {
                Destroy(_mesh);
                _mesh = null;
            }
        }

        void OnDestroy()
        {
            Clear();
            if (_material != null) Destroy(_material);
        }
    }
}

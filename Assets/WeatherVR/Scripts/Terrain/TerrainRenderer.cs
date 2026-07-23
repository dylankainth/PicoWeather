using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Terrain
{
    /// <summary>
    /// Builds and owns the terrain mesh, its LOD chain and its material.
    ///
    /// Sits directly under the map root, so it inherits the map's placement and
    /// scale and its own transform stays identity — everything is expressed in the
    /// normalised [-0.5, 0.5] map space that <see cref="GeoBounds"/> produces.
    /// </summary>
    public class TerrainRenderer : MonoBehaviour
    {
        [Tooltip("Screen-relative height at which each LOD hands over to the next.")]
        public float[] LodTransitions = { 0.35f, 0.12f, 0.03f };

        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        Material _material;
        Mesh[] _meshes;
        LODGroup _lodGroup;

        public Material Material => _material;
        public Bounds LocalBounds { get; private set; }

        /// <summary>Rebuilds everything for a new snapshot.</summary>
        public void Apply(WeatherSnapshot snapshot, AppConfig config)
        {
            if (snapshot?.Terrain == null)
            {
                Debug.LogError("[WeatherVR] TerrainRenderer got no heightfield.");
                return;
            }

            EnsureMaterial();
            if (_material != null && snapshot.Satellite != null)
                _material.SetTexture(MainTexId, snapshot.Satellite);

            Clear();

            _meshes = TerrainMeshBuilder.BuildLodChain(snapshot.Terrain, config);
            var renderers = new Renderer[_meshes.Length];

            for (int i = 0; i < _meshes.Length; i++)
            {
                var child = new GameObject($"TerrainLOD{i}");
                child.transform.SetParent(transform, worldPositionStays: false);

                var filter = child.AddComponent<MeshFilter>();
                filter.sharedMesh = _meshes[i];

                var meshRenderer = child.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = _material;
                // The map is 2 m across and lit by a single key light; casting shadows
                // from it costs a full extra pass for no visible gain.
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;

                renderers[i] = meshRenderer;
            }

            LocalBounds = _meshes[0].bounds;

            _lodGroup = GetComponent<LODGroup>();
            if (_lodGroup == null) _lodGroup = gameObject.AddComponent<LODGroup>();

            var lods = new LOD[_meshes.Length];
            for (int i = 0; i < _meshes.Length; i++)
            {
                float transition = i < LodTransitions.Length ? LodTransitions[i] : 0.01f;
                lods[i] = new LOD(transition, new[] { renderers[i] });
            }
            _lodGroup.SetLODs(lods);
            _lodGroup.RecalculateBounds();

            int triangles = _meshes[0].triangles.Length / 3;
            Debug.Log($"[WeatherVR] Terrain built: {triangles:N0} triangles at LOD0, " +
                      $"{snapshot.Terrain}");
        }

        void EnsureMaterial()
        {
            if (_material != null) return;

            var shader = Shader.Find("WeatherVR/TerrainSurface");
            if (shader == null)
            {
                Debug.LogError("[WeatherVR] TerrainSurface shader is missing from the build.");
                return;
            }

            _material = new Material(shader) { name = "TerrainSurface (runtime)" };
        }

        void Clear()
        {
            if (_meshes != null)
            {
                foreach (var mesh in _meshes)
                    if (mesh != null) Destroy(mesh);
                _meshes = null;
            }

            for (int i = transform.childCount - 1; i >= 0; i--)
                Destroy(transform.GetChild(i).gameObject);
        }

        void OnDestroy()
        {
            Clear();
            if (_material != null) Destroy(_material);
        }
    }
}

using UnityEngine;

namespace WeatherVR.Terrain
{
    /// <summary>
    /// Owns the liquid-glass plinth mesh under the map. Needs no weather snapshot --
    /// it is pure geometry -- so it builds itself once in <see cref="Awake"/>.
    /// Sits under the map root alongside <see cref="TerrainRenderer"/>, so it
    /// inherits the map's placement and scale for free.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class PedestalRenderer : MonoBehaviour
    {
        Material _material;

        void Awake()
        {
            var filter = GetComponent<MeshFilter>();
            if (filter.sharedMesh == null)
                filter.sharedMesh = PedestalMeshBuilder.Build();

            var meshRenderer = GetComponent<MeshRenderer>();
            EnsureMaterial(meshRenderer);

            // A static, always-lit prop: no shadow pass costs anything visible on a
            // 2 m tabletop, matching every other renderer under the map root.
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        void EnsureMaterial(MeshRenderer meshRenderer)
        {
            if (_material != null) return;

            var shader = Shader.Find("WeatherVR/Pedestal");
            if (shader == null)
            {
                Debug.LogError("[WeatherVR] Pedestal shader is missing from the build.");
                return;
            }

            _material = new Material(shader) { name = "Pedestal (runtime)" };

            // The kaleidoscope/plain-xyz fallback lever is still a continuous float
            // rather than a shader keyword -- every material here is created at
            // runtime via Shader.Find with no material asset backing it, so a
            // shader_feature variant would have nothing to keep it alive and would
            // be stripped from the device build. It is no longer set here, though:
            // EnvironmentController publishes it (and the segment count and spin) as
            // _WVRKaleido* globals, because the plinth has to fold identically to
            // the sky it reflects and the floor beside it.
            meshRenderer.sharedMaterial = _material;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}

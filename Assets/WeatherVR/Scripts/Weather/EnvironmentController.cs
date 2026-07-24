using UnityEngine;

namespace WeatherVR.Weather
{
    /// <summary>
    /// The glass surround that replaces the bland black around the tabletop.
    ///
    /// Two parts:
    ///   • the studio skybox, whose gradient/sun colours are re-tinted per scene
    ///     (owned here so one place decides what the surround looks like),
    ///   • a large frosted-glass floor disc under the user that gently tracks the
    ///     head on the ground plane, so wherever you stand you are standing on glass
    ///     rather than on nothing.
    ///
    /// The disc follows only the head's horizontal position (never its rotation or
    /// height), which keeps it feeling like a fixed floor while still being centred
    /// under a user who walks around the tracked space.
    /// </summary>
    public sealed class EnvironmentController : MonoBehaviour
    {
        [Tooltip("Head transform the glass floor stays centred under. Falls back to the main camera.")]
        public Transform Head;

        [Tooltip("Radius of the frosted-glass floor disc, in metres.")]
        public float FloorRadius = 6f;

        [Tooltip("How far below the head the glass floor sits, in metres. Roughly standing eye-to-floor.")]
        public float FloorDropBelowHead = 1.35f;

        [Tooltip("Segments around the disc. 48 is smooth at this size and trivially cheap.")]
        public int FloorSegments = 48;

        Material _floorMaterial;
        Material _skyMaterial;
        Transform _floor;

        static readonly int ColorInnerId = Shader.PropertyToID("_ColorA");
        static readonly int ColorOuterId = Shader.PropertyToID("_ColorB");
        static readonly int ZenithId = Shader.PropertyToID("_Zenith");
        static readonly int HorizonId = Shader.PropertyToID("_Horizon");
        static readonly int NadirId = Shader.PropertyToID("_Nadir");
        static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        static readonly int SunGlowId = Shader.PropertyToID("_SunGlow");
        static readonly int SunDirId = Shader.PropertyToID("_SunDir");

        void Awake()
        {
            if (Head == null && Camera.main != null) Head = Camera.main.transform;
            EnsureSky();
            BuildFloor();
        }

        /// <summary>
        /// Caches the studio-sky material, creating one if the scene has none. A
        /// runtime-created skybox material assigned at scene-build time is not always
        /// preserved through save/load, and a null skybox falls back to the camera's
        /// flat clear colour — i.e. the old black. This makes the glass sky the
        /// guaranteed surround regardless.
        /// </summary>
        void EnsureSky()
        {
            _skyMaterial = RenderSettings.skybox;
            if (_skyMaterial != null) return;

            var shader = Shader.Find("WeatherVR/StudioSky");
            if (shader == null)
            {
                Debug.LogWarning("[WeatherVR] StudioSky shader missing; sky left at the default.");
                return;
            }
            _skyMaterial = new Material(shader) { name = "StudioSky (environment runtime)" };
            RenderSettings.skybox = _skyMaterial;
        }

        void BuildFloor()
        {
            var shader = Shader.Find("WeatherVR/GlassEnvironment");
            if (shader == null)
            {
                Debug.LogWarning("[WeatherVR] GlassEnvironment shader missing; no glass floor drawn.");
                return;
            }

            var floorObject = new GameObject("GlassFloor");
            floorObject.transform.SetParent(transform, false);
            _floor = floorObject.transform;

            var filter = floorObject.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildDisc(FloorRadius, Mathf.Max(8, FloorSegments));

            _floorMaterial = new Material(shader) { name = "GlassFloor (runtime)" };
            var meshRenderer = floorObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _floorMaterial;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        void LateUpdate()
        {
            if (_floor == null) return;
            if (Head == null && Camera.main != null) Head = Camera.main.transform;
            if (Head == null) return;

            // Horizontal follow only: a floor that pitched or spun with the head would
            // read as a moving platform, not as ground.
            Vector3 p = Head.position;
            _floor.position = new Vector3(p.x, p.y - FloorDropBelowHead, p.z);
            _floor.rotation = Quaternion.identity;
        }

        /// <summary>Applies a scene's glass palette to the floor and the studio sky.</summary>
        public void Apply(WeatherSceneProfile profile, Vector3 sunDirection)
        {
            if (_floorMaterial != null)
            {
                _floorMaterial.SetColor(ColorInnerId, profile.GlassInner);
                _floorMaterial.SetColor(ColorOuterId, profile.GlassOuter);
            }

            if (_skyMaterial == null) _skyMaterial = RenderSettings.skybox;
            if (_skyMaterial != null)
            {
                _skyMaterial.SetColor(ZenithId, profile.SkyZenith);
                _skyMaterial.SetColor(HorizonId, profile.SkyHorizon);
                _skyMaterial.SetColor(NadirId, profile.SkyNadir);
                _skyMaterial.SetColor(SunColorId, profile.SunGlow);
                _skyMaterial.SetFloat(SunGlowId, profile.SunGlowStrength);
                _skyMaterial.SetVector(SunDirId, sunDirection);
            }
        }

        /// <summary>A flat disc in the XZ plane, radius R, centred at the origin.</summary>
        static Mesh BuildDisc(float radius, int segments)
        {
            var vertices = new Vector3[segments + 1];
            var uvs = new Vector2[segments + 1];
            var normals = new Vector3[segments + 1];
            var triangles = new int[segments * 3];

            vertices[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            normals[0] = Vector3.up;

            for (int i = 0; i < segments; i++)
            {
                float a = (i / (float)segments) * Mathf.PI * 2f;
                float cx = Mathf.Cos(a);
                float cz = Mathf.Sin(a);
                vertices[i + 1] = new Vector3(cx * radius, 0f, cz * radius);
                // uv maps the rim to unit radius so the shader can use length(uv*2-1).
                uvs[i + 1] = new Vector2((cx + 1f) * 0.5f, (cz + 1f) * 0.5f);
                normals[i + 1] = Vector3.up;

                int t = i * 3;
                triangles[t] = 0;
                triangles[t + 1] = i + 1;
                triangles[t + 2] = (i + 1) % segments + 1;
            }

            var mesh = new Mesh { name = "GlassFloorDisc" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        void OnDestroy()
        {
            if (_floorMaterial != null) Destroy(_floorMaterial);
        }
    }
}

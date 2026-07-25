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
        static readonly int HeightTexId = Shader.PropertyToID("_HeightTex");
        static readonly int LevelMetersId = Shader.PropertyToID("_LevelMeters");
        static readonly int MinElevationId = Shader.PropertyToID("_MinElevation");
        static readonly int ElevationRangeId = Shader.PropertyToID("_ElevationRange");
        static readonly int ShallowColorId = Shader.PropertyToID("_ShallowColor");
        static readonly int DeepColorId = Shader.PropertyToID("_DeepColor");
        static readonly int DeepDepthId = Shader.PropertyToID("_DeepDepth");
        static readonly int FoamDepthId = Shader.PropertyToID("_FoamDepth");
        static readonly int ConnectTexId = Shader.PropertyToID("_ConnectTex");
        static readonly int ConnectMinId = Shader.PropertyToID("_ConnectMin");
        static readonly int ConnectRangeId = Shader.PropertyToID("_ConnectRange");

        [Tooltip("Water tint and translucency (alpha). Kept for the shader's own " +
                 "shoreline rim/sparkle terms; shallow/deep shading below is what " +
                 "actually reads as depth.")]
        public Color WaterColor = new Color(0.18f, 0.42f, 0.62f, 0.55f);

        [Tooltip("Storm-surge presets, metres above the lowest point in this terrain " +
                 "tile. Index 0 is treated as \"off\" — there is nothing to show at +0. " +
                 "Mirrored by WeatherCarouselBuilder.FloodPresets, which supplies the " +
                 "button labels; the two are not coupled and must be kept in step by hand.")]
        public float[] SurgePresetsMeters = { 0f, 2f, 5f, 10f };

        [Tooltip("Seconds for the water to rise or recede from one level to the next.")]
        public float RiseSeconds = 2.5f;

        [Tooltip("Colour of water at (or above) DeepDepthMeters.")]
        public Color ShallowColor = new Color(0.55f, 0.78f, 0.80f, 0.30f);

        [Tooltip("Colour of water at (or above) DeepDepthMeters.")]
        public Color DeepColor = new Color(0.05f, 0.16f, 0.28f, 0.80f);

        [Tooltip("Depth in metres at which water reads as fully \"deep\".")]
        public float DeepDepthMeters = 10f;

        [Tooltip("Depth in metres within which a foam/shoreline band is drawn.")]
        public float FoamDepthMeters = 1f;

        [Tooltip("Resolution of the baked terrain-height lookup texture the shader " +
                 "samples to find the true shoreline.")]
        [Range(64, 1024)] public int HeightTextureResolution = 512;

        Material _material;
        Mesh _mesh;
        MeshFilter _filter;
        MeshRenderer _renderer;
        Texture2D _heightTexture;
        Texture2D _connectTexture;
        AppConfig _config;
        TerrainHeightfield _terrain;
        FloodConnectivityField _flood;
        float _minElevation;
        bool _hasSnapshot;

        // Animation state: the water rises/recedes from _displayed toward _target over
        // RiseSeconds rather than snapping, so a storm surge reads as rising water
        // instead of a light switching on. Both are metres asl; null means "hidden".
        float? _target;
        float? _displayed;
        float _fromMeters;
        float _toMeters;
        float _elapsed;
        bool _animating;

        /// <summary>Target surge level, metres above sea level. Null while hidden. This is
        /// where the water is headed, not necessarily where it is drawn right now —
        /// see <see cref="DisplayedLevelMeters"/>.</summary>
        public float? LevelMeters => _target;

        /// <summary>The level currently being drawn, metres above sea level, mid-animation
        /// included. Null only once the recede animation has finished and the plane is
        /// fully hidden.</summary>
        public float? DisplayedLevelMeters => _displayed;

        /// <summary>Lowest elevation sampled in the current snapshot's terrain, metres asl.</summary>
        public float MinElevationMeters => _minElevation;

        /// <summary>
        /// Caches the config and terrain range for a new snapshot and (re)builds the
        /// plane. Does not itself show water — the user opts into a surge level from
        /// the carousel via <see cref="SetSurge"/>. Resets immediately (no animation):
        /// a fresh snapshot has nothing to animate from.
        /// </summary>
        public void Apply(WeatherSnapshot snapshot, AppConfig config)
        {
            _config = config;
            _terrain = snapshot?.Terrain;
            _flood = snapshot?.Flood;
            _hasSnapshot = _terrain != null;
            _minElevation = _hasSnapshot ? _terrain.MinElevation : 0f;

            EnsureMaterial();
            EnsureComponents();
            EnsureMesh();
            EnsureHeightTexture();
            EnsureConnectivityTexture();

            SetLevelMetersImmediate(null);
        }

        /// <summary>
        /// Selects a surge preset by index (clamped). Index 0 (or any preset ≤ 0)
        /// hides the water. Animates toward the new level rather than snapping.
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
        /// Sets the target surge level and starts (or retargets) the rise/recede
        /// animation from wherever the water is currently displayed. Null (or a level
        /// at/below the lowest terrain sample) recedes the water fully out of view.
        /// </summary>
        public void SetLevelMeters(float? metersAsl)
        {
            _target = metersAsl;

            if (_renderer == null) return;

            float to = (metersAsl.HasValue && metersAsl.Value > _minElevation)
                ? metersAsl.Value
                : _minElevation;

            // Already hidden and asked to hide again (e.g. leaving a non-storm day
            // repeatedly calls SetSurge(0)) — nothing to animate, and starting a no-op
            // animation would only leave the plane at _minElevation for a full
            // RiseSeconds before settling back to null.
            if (!_displayed.HasValue && to <= _minElevation)
            {
                _animating = false;
                return;
            }

            float from = _displayed ?? _minElevation;

            _fromMeters = from;
            _toMeters = to;
            _elapsed = 0f;
            _animating = true;

            // Rising (or already visible while receding) needs the renderer on for the
            // animation to be seen at all; a fully-hidden recede switches it off only
            // once the animation reaches the floor, in Update().
            if (to > from || _displayed.HasValue)
                _renderer.enabled = true;

            if (!_displayed.HasValue)
                _displayed = from;
        }

        /// <summary>
        /// Sets the displayed level immediately, with no animation. Used only when a
        /// new snapshot arrives — there is no prior water level to animate from.
        /// </summary>
        void SetLevelMetersImmediate(float? metersAsl)
        {
            _target = metersAsl;
            _animating = false;
            _displayed = metersAsl.HasValue && metersAsl.Value > _minElevation
                ? metersAsl
                : null;

            ApplyDisplayedLevel();
        }

        void Update()
        {
            if (!_animating || _renderer == null) return;

            _elapsed += Time.unscaledDeltaTime;
            float t = RiseSeconds > 0f ? Mathf.Clamp01(_elapsed / RiseSeconds) : 1f;
            float eased = t * t * (3f - 2f * t); // smoothstep, matches EarthIntroFeature's Animate

            float level = Mathf.Lerp(_fromMeters, _toMeters, eased);
            _displayed = level;
            ApplyDisplayedLevel();

            if (t >= 1f)
            {
                _animating = false;
                if (_toMeters <= _minElevation)
                {
                    _renderer.enabled = false;
                    _displayed = null;
                }
            }
        }

        void ApplyDisplayedLevel()
        {
            if (_renderer == null) return;

            if (!_displayed.HasValue || !_hasSnapshot || _config == null)
            {
                _renderer.enabled = false;
                return;
            }

            float level = _displayed.Value;
            _renderer.enabled = level > _minElevation;

            float localY = _config.TerrainElevationToMapUnits(level);
            transform.localPosition = new Vector3(0f, localY, 0f);

            if (_material != null)
                _material.SetFloat(LevelMetersId, level);
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
            _material.SetColor(ShallowColorId, ShallowColor);
            _material.SetColor(DeepColorId, DeepColor);
            _material.SetFloat(DeepDepthId, Mathf.Max(0.01f, DeepDepthMeters));
            _material.SetFloat(FoamDepthId, Mathf.Max(0f, FoamDepthMeters));
            _material.SetFloat(MinElevationId, _minElevation);
            _material.SetFloat(ElevationRangeId, _hasSnapshot ? _terrain.ElevationRange : 1f);
        }

        /// <summary>
        /// Bakes the terrain heightfield into a single-channel lookup texture in the
        /// same normalised (u,v) the mesh's UVs use, so <c>Water.shader</c> can find the
        /// true shoreline instead of drawing a flat rectangle. Built once per snapshot —
        /// same sampling idiom as <see cref="ProceduralSatellite.Generate"/>, since
        /// TerrainHeightfield exposes no raw array, only per-pixel sampling.
        /// </summary>
        void EnsureHeightTexture()
        {
            if (_material == null) return;

            if (_heightTexture != null)
            {
                Destroy(_heightTexture);
                _heightTexture = null;
            }

            if (!_hasSnapshot)
            {
                _material.SetTexture(HeightTexId, Texture2D.whiteTexture);
                return;
            }

            int resolution = Mathf.Clamp(HeightTextureResolution, 64, 1024);
            TextureFormat format = SystemInfo.SupportsTextureFormat(TextureFormat.RHalf)
                ? TextureFormat.RHalf
                : TextureFormat.R8;
            if (format == TextureFormat.R8)
                Debug.Log("[WeatherVR] RHalf unsupported; falling back to R8 for the flood height texture.");

            _heightTexture = new Texture2D(resolution, resolution, format, mipChain: false, linear: true)
            {
                name = "FloodHeight (runtime)",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color[resolution * resolution];
            for (int y = 0; y < resolution; y++)
            {
                float v = y / (float)(resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    float u = x / (float)(resolution - 1);
                    float elevation = _terrain.SampleElevation(u, v);
                    float normalized = Mathf.Clamp01((elevation - _minElevation) / _terrain.ElevationRange);
                    pixels[y * resolution + x] = new Color(normalized, normalized, normalized, 1f);
                }
            }

            _heightTexture.SetPixels(pixels);
            _heightTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            _material.SetTexture(HeightTexId, _heightTexture);
            _material.SetFloat(MinElevationId, _minElevation);
            _material.SetFloat(ElevationRangeId, _terrain.ElevationRange);
        }

        /// <summary>
        /// Bakes <see cref="FloodConnectivityField"/> into a second lookup texture, same
        /// resolution and (u,v) convention as <see cref="EnsureHeightTexture"/>, so
        /// <c>Water.shader</c> can gate on real hydraulic connectivity — a cell only
        /// floods once the water level reaches its connection level, which already
        /// accounts for real Thames defence crest heights, not merely on being below the
        /// water plane. Falls back to a texture that never blocks anything if no flood
        /// field is present, so an old snapshot with no <see cref="FloodConnectivityField"/>
        /// degrades to the plain depth-clip behaviour rather than hiding all water.
        /// </summary>
        void EnsureConnectivityTexture()
        {
            if (_material == null) return;

            if (_connectTexture != null)
            {
                Destroy(_connectTexture);
                _connectTexture = null;
            }

            if (_flood == null || !_flood.IsValid)
            {
                // White = normalised 1.0 everywhere; paired with ConnectRange = 0 this
                // makes every pixel's decoded connect level equal ConnectMin, i.e.
                // "already connected at the lowest level this field could represent" —
                // never the blocking factor, so the terrain-height clip alone decides.
                _material.SetTexture(ConnectTexId, Texture2D.whiteTexture);
                _material.SetFloat(ConnectMinId, _minElevation);
                _material.SetFloat(ConnectRangeId, 0f);
                return;
            }

            int resolution = Mathf.Clamp(HeightTextureResolution, 64, 1024);
            TextureFormat format = SystemInfo.SupportsTextureFormat(TextureFormat.RHalf)
                ? TextureFormat.RHalf
                : TextureFormat.R8;

            _connectTexture = new Texture2D(resolution, resolution, format, mipChain: false, linear: true)
            {
                name = "FloodConnectivity (runtime)",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            float connectMin = _flood.BaseMeters;
            float connectRange = Mathf.Max(0.001f, _flood.CapMeters - _flood.BaseMeters);

            var pixels = new Color[resolution * resolution];
            for (int y = 0; y < resolution; y++)
            {
                float v = y / (float)(resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    float u = x / (float)(resolution - 1);
                    float connectLevel = _flood.SampleConnectLevel(u, v);
                    // PositiveInfinity (never connects within the baked window) clamps to
                    // 1.0 -- decoded back to ConnectMin + ConnectRange, i.e. the cap, which
                    // is already far above any surge preset this app offers.
                    float normalized = float.IsInfinity(connectLevel)
                        ? 1f
                        : Mathf.Clamp01((connectLevel - connectMin) / connectRange);
                    pixels[y * resolution + x] = new Color(normalized, normalized, normalized, 1f);
                }
            }

            _connectTexture.SetPixels(pixels);
            _connectTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            _material.SetTexture(ConnectTexId, _connectTexture);
            _material.SetFloat(ConnectMinId, connectMin);
            _material.SetFloat(ConnectRangeId, connectRange);
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
            if (_heightTexture != null) Destroy(_heightTexture);
            if (_connectTexture != null) Destroy(_connectTexture);
        }
    }
}

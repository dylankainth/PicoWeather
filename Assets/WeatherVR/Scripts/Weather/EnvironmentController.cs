using UnityEngine;

namespace WeatherVR.Weather
{
    /// <summary>
    /// Owns the whole surround: the studio-sky skybox and the world-locked glass
    /// floor the plinth stands on. Guarantees both exist at runtime (a null skybox
    /// falls back to the camera's flat clear colour -- the old black; a missing
    /// floor is the empty void the app shipped with before this pass), and
    /// re-tints them per weather scene so the space around the tabletop shifts
    /// with the weather: bright for clear, grey for overcast, dark violet for a
    /// thunderstorm.
    ///
    /// Also the single publisher of the environment palette as shader globals
    /// (_WVRSkyZenith etc.) -- every glass surface that reflects the sky
    /// (Pedestal.shader, GlassSurround.shader) reads those globals rather than
    /// duplicating the palette, so they can never drift from the sky itself.
    ///
    /// A glass floor disc existed once and was removed; it is reintroduced here,
    /// deliberately opaque (see GlassSurround.shader's header) rather than blended,
    /// so it costs nothing extra against the skybox it replaces.
    /// </summary>
    public sealed class EnvironmentController : MonoBehaviour
    {
        /// <summary>The map root, used only to align the floor under the table in XZ.
        /// Wired by WeatherSceneBootstrap at runtime and by SceneBuilder for a
        /// freshly generated scene.</summary>
        public Transform MapRoot;

        Material _skyMaterial;
        Material _floorMaterial;
        MeshRenderer _floorRenderer;

        static readonly int ZenithId = Shader.PropertyToID("_Zenith");
        static readonly int HorizonId = Shader.PropertyToID("_Horizon");
        static readonly int NadirId = Shader.PropertyToID("_Nadir");
        static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        static readonly int SunGlowId = Shader.PropertyToID("_SunGlow");
        static readonly int SunDirId = Shader.PropertyToID("_SunDir");
        static readonly int PrismColorId = Shader.PropertyToID("_PrismColor");
        static readonly int PrismStrengthId = Shader.PropertyToID("_PrismStrength");
        static readonly int LatticeStrengthId = Shader.PropertyToID("_LatticeStrength");

        static readonly int FloorColorId = Shader.PropertyToID("_FloorColor");
        static readonly int GridColorId = Shader.PropertyToID("_GridColor");
        static readonly int RimColorId = Shader.PropertyToID("_RimColor");

        // Global environment palette, read by Pedestal.shader and
        // GlassSurround.shader. Declared as globals (not per-material properties)
        // in both shaders specifically so this is the only place that sets them.
        static readonly int WvrSkyZenithId = Shader.PropertyToID("_WVRSkyZenith");
        static readonly int WvrSkyHorizonId = Shader.PropertyToID("_WVRSkyHorizon");
        static readonly int WvrSkyNadirId = Shader.PropertyToID("_WVRSkyNadir");
        static readonly int WvrSkySharpnessId = Shader.PropertyToID("_WVRSkySharpness");
        static readonly int WvrSunColorId = Shader.PropertyToID("_WVRSunColor");
        static readonly int WvrSunGlowId = Shader.PropertyToID("_WVRSunGlow");
        static readonly int WvrSunSharpId = Shader.PropertyToID("_WVRSunSharp");
        static readonly int WvrSunDirId = Shader.PropertyToID("_WVRSunDir");

        // The shared kaleidoscope layout, published for the same reason and in the
        // same way as the palette above: the skybox, the glass floor and the plinth
        // each fold their environment sampling, and they have to fold *identically*
        // or a facet's reflection stops matching the sky it reflects.
        static readonly int WvrKaleidoAmountId = Shader.PropertyToID("_WVRKaleidoAmount");
        static readonly int WvrKaleidoSegmentsId = Shader.PropertyToID("_WVRKaleidoSegments");
        static readonly int WvrKaleidoSpinId = Shader.PropertyToID("_WVRKaleidoSpin");

        void Awake()
        {
            // Before anything draws: these are globals, and an unpublished global
            // reads 0. For the amount that silently means "plain .xyz" (a safe
            // degradation, and the documented fallback); for the segment count it
            // would mean a divide by zero, which is why KaleidoSegments() in
            // WeatherVRGlass.cginc clamps rather than trusting the publisher.
            PublishKaleidoscope();
            EnsureSky();
            EnsureSurround();
        }

        /// <summary>
        /// Pushes <see cref="Core.AppConfig.PedestalKaleidoscope"/> and friends out as
        /// shader globals. Called from <c>Awake</c> and again on every weather change,
        /// so editing the config asset while playing takes effect on the next card tap.
        /// </summary>
        void PublishKaleidoscope()
        {
            var config = Core.AppConfig.Instance;
            Shader.SetGlobalFloat(WvrKaleidoAmountId, config.PedestalKaleidoscope);
            Shader.SetGlobalFloat(WvrKaleidoSegmentsId, config.KaleidoSegments);
            Shader.SetGlobalFloat(WvrKaleidoSpinId, config.KaleidoSpin);
        }

        void LateUpdate()
        {
            // Cheap and idempotent: keeps the floor aligned under the table as the map
            // root moves. The map now follows the head (see ComfortFollow, added by
            // SceneBuilder.Populate / WeatherSceneBootstrap.EnsureMapFollow), so this
            // is load-bearing, not just a hedge against a hypothetical future change.
            if (MapRoot != null && _floorRenderer != null)
            {
                Vector3 p = MapRoot.position;
                transform.position = new Vector3(p.x, transform.position.y, p.z);
            }
        }

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

        void EnsureSurround()
        {
            if (_floorRenderer != null) return;

            var existing = transform.Find("GlassFloor");
            GameObject floorObject = existing != null ? existing.gameObject : new GameObject("GlassFloor");
            floorObject.transform.SetParent(transform, worldPositionStays: false);
            floorObject.transform.localPosition = Vector3.zero;

            var filter = floorObject.GetComponent<MeshFilter>();
            if (filter == null) filter = floorObject.AddComponent<MeshFilter>();
            if (filter.sharedMesh == null) filter.sharedMesh = SurroundMeshBuilder.BuildFloorQuad();

            _floorRenderer = floorObject.GetComponent<MeshRenderer>();
            if (_floorRenderer == null) _floorRenderer = floorObject.AddComponent<MeshRenderer>();

            if (_floorMaterial == null)
            {
                // Resources.Load first: a Resources-folder shader is never stripped
                // from a player build regardless of the always-included list, which
                // matters because this shader is new and easy to forget registering.
                // Shader.Find as a belt-and-braces fallback for the editor.
                Shader shader = Resources.Load<Shader>("GlassSurround");
                if (shader == null) shader = Shader.Find("WeatherVR/GlassSurround");
                if (shader == null)
                {
                    Debug.LogError("[WeatherVR] GlassSurround shader is missing from the build.");
                    return;
                }
                _floorMaterial = new Material(shader) { name = "GlassSurround (runtime)" };
                // No per-material kaleidoscope set here any more -- the floor reads
                // the _WVRKaleido* globals PublishKaleidoscope() writes, so there is
                // exactly one source for the fold.
            }

            _floorRenderer.sharedMaterial = _floorMaterial;
            _floorRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _floorRenderer.receiveShadows = false;
        }

        /// <summary>
        /// Re-tints the sky gradient, the floor and the shared environment globals for a
        /// weather scene at a given time of day.
        ///
        /// <paramref name="daylight"/> is 0 (night) .. 1 (full day), supplied by
        /// <see cref="WeatherSceneDirector"/> from the hour on the carousel's time
        /// slider. It defaults to 1 so a caller that does not care about the clock
        /// still gets a fully lit surround.
        /// </summary>
        public void Apply(WeatherSceneProfile profile, Vector3 sunDirection, float daylight = 1f)
        {
            PublishKaleidoscope();

            if (_skyMaterial == null) _skyMaterial = RenderSettings.skybox;

            // Two gradings, in this order: how heavy the sky is (weather), then how
            // much light is left in it (hour).
            float storm = Mathf.Clamp01(profile.CloudDarkness);
            Color zenith = Color.Lerp(
                new Color(0.080f, 0.055f, 0.125f),
                new Color(0.025f, 0.018f, 0.045f),
                storm * 0.72f);
            Color horizon = Color.Lerp(
                new Color(0.190f, 0.125f, 0.215f),
                new Color(0.060f, 0.045f, 0.085f),
                storm * 0.74f);
            Color nadir = Color.Lerp(
                new Color(0.030f, 0.024f, 0.045f),
                new Color(0.012f, 0.010f, 0.020f),
                storm * 0.70f);

            zenith = Color.Lerp(zenith, profile.SunGlow, 0.08f);
            horizon = Color.Lerp(horizon, profile.SunGlow, 0.11f);

            // Night keeps a little blue rather than crushing to black: the terrain
            // silhouette and the kaleidoscope seams both have to stay readable at
            // 03:00, which is a demo requirement physics would not give us.
            float light = Mathf.Clamp01(daylight);
            Color nightTint = new Color(0.055f, 0.070f, 0.130f);
            zenith = Color.Lerp(nightTint * 0.55f, zenith, light);
            horizon = Color.Lerp(nightTint, horizon, light);
            nadir = Color.Lerp(nightTint * 0.35f, nadir, light);
            float glowStrength = profile.SunGlowStrength * 0.62f * Mathf.Lerp(0.10f, 1f, light);

            if (_skyMaterial != null)
            {
                _skyMaterial.SetColor(ZenithId, zenith);
                _skyMaterial.SetColor(HorizonId, horizon);
                _skyMaterial.SetColor(NadirId, nadir);
                _skyMaterial.SetColor(SunColorId, profile.SunGlow);
                _skyMaterial.SetFloat(SunGlowId, glowStrength);
                _skyMaterial.SetVector(SunDirId, sunDirection);

                // Keep the surround weather-reactive, but within a narrow, comfortable
                // range so changing cards never produces an abrupt peripheral flash.
                Color violet = new Color(0.52f, 0.26f, 0.57f);
                Color prism = Color.Lerp(violet, profile.SunGlow, 0.18f);
                _skyMaterial.SetColor(PrismColorId, prism);
                _skyMaterial.SetFloat(
                    PrismStrengthId,
                    Mathf.Lerp(0.22f, 0.29f, storm));
                _skyMaterial.SetFloat(
                    LatticeStrengthId,
                    Mathf.Lerp(0.055f, 0.075f, storm));
            }

            if (_floorMaterial != null)
            {
                _floorMaterial.SetColor(FloorColorId, nadir);
                _floorMaterial.SetColor(GridColorId, horizon);
                _floorMaterial.SetColor(RimColorId, profile.SunGlow * Mathf.Lerp(0.35f, 1f, light));
            }

            // Published once here so every glass surface (pedestal, floor, and any
            // later addition) reflects the same sky without re-deriving it.
            //
            // Note these are the GRADED colours, not profile.Sky*: the skybox draws the
            // storm/night-graded gradient, so publishing the raw profile values would
            // have every glass surface reflecting a sky that is not the one overhead --
            // exactly the drift this single-publisher arrangement exists to prevent.
            Shader.SetGlobalColor(WvrSkyZenithId, zenith);
            Shader.SetGlobalColor(WvrSkyHorizonId, horizon);
            Shader.SetGlobalColor(WvrSkyNadirId, nadir);
            Shader.SetGlobalFloat(WvrSkySharpnessId, 2.2f);
            Shader.SetGlobalColor(WvrSunColorId, profile.SunGlow);
            Shader.SetGlobalFloat(WvrSunGlowId, glowStrength);
            Shader.SetGlobalFloat(WvrSunSharpId, 18f);
            Shader.SetGlobalVector(WvrSunDirId, sunDirection);
        }
    }
}

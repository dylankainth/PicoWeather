using UnityEngine;

namespace WeatherVR.Core
{
    /// <summary>
    /// Single source of truth for every tunable in the app: the geographic region,
    /// the tabletop scale, resolution budgets and the procedural seed.
    ///
    /// Loaded from <c>Resources/WeatherVRConfig.asset</c> when present so it can be
    /// tweaked in the inspector without touching code; falls back to the defaults
    /// baked in below so the app still runs from a bare checkout.
    /// </summary>
    [CreateAssetMenu(menuName = "WeatherVR/App Config", fileName = "WeatherVRConfig")]
    public class AppConfig : ScriptableObject
    {
        public const string ResourcePath = "WeatherVRConfig";

        // ---------------------------------------------------------------- region

        [Header("Region (City of London)")]
        [Tooltip("Latitude of the map centre, degrees north.")]
        public double CenterLatitude = 51.5136;   // the Gherkin / Leadenhall skyscraper cluster

        [Tooltip("Longitude of the map centre, degrees east.")]
        public double CenterLongitude = -0.0832;

        [Tooltip("Edge length of the square region covered by the map, in kilometres. " +
                 "City-block scale, not the 50 km regional view Shanghai used: London's " +
                 "relief is a couple of metres and invisible at any honest scale, so this " +
                 "region is sized for its buildings instead.")]
        public double RegionSpanKm = 5.0;

        // -------------------------------------------------------------- buildings

        [Header("Buildings")]
        [Tooltip("Hard cap on building count actually built into the mesh, applied on top " +
                 "of whatever cap the bake already applied. A second, cheap backstop: a " +
                 "hand-edited buildings.json cannot blow the triangle budget silently.")]
        [Range(50, 2000)] public int MaxBuildings = 600;

        // ----------------------------------------------------------------- scale

        [Header("Tabletop scale")]
        [Tooltip("Edge length of the map in VR metres. 2 m across a 50 km region " +
                 "means 1 VR metre = 25 km.")]
        public float MapSizeMeters = 2.0f;

        [Tooltip("Vertical exaggeration relative to true scale. 1.0 renders altitude " +
                 "at exactly the same scale as horizontal distance. Values above 1 make " +
                 "the atmosphere read as volumetric rather than as a flat film. This is " +
                 "scaled to RegionSpanKm, not an absolute constant: Horizontal (and so " +
                 "Vertical) is inversely proportional to the span, so shrinking the " +
                 "region without rescaling this multiplies cloud height by the same " +
                 "factor the span shrank by. Retune whenever RegionSpanKm changes.")]
        public float VerticalExaggeration = 0.4f;

        [Tooltip("Extra vertical exaggeration applied to terrain relief only, on top " +
                 "of VerticalExaggeration. Same caveat as VerticalExaggeration: it is " +
                 "tuned for the current RegionSpanKm, not region-independent. This " +
                 "shipped wrong once already -- left at the Shanghai-era value (12, for " +
                 "a 50 km span) after the region shrank to London's 5 km span, so the " +
                 "already-10x-larger Horizontal scale multiplied with it and turned a " +
                 "43 m hill into an 84 cm spike. tools/verify_logic/Verify.cs checks " +
                 "this against the real baked terrain.bin peak.")]
        public float TerrainReliefExaggeration = 1.2f;

        // ------------------------------------------------------------ atmosphere

        [Header("Atmosphere")]
        [Tooltip("Altitude in metres below which no cloud is rendered.")]
        public float AtmosphereFloorMeters = 200f;

        [Tooltip("Altitude in metres of the top of the rendered cloud volume. " +
                 "3.5 km covers the low/mid deck and clips out the high (cirrus) " +
                 "layer. Was 12 km (full atmosphere) — on the 5 km London span that " +
                 "made the box read as an almost cube-shaped tower rather than a " +
                 "tabletop cloud deck; see CLAUDE.md 'tall and weird clouds'.")]
        public float AtmosphereCeilingMeters = 3500f;

        // ----------------------------------------------------------- resolutions

        [Header("Resolution budgets")]
        [Tooltip("Vertices per side of the terrain mesh at LOD0. 128 gives ~32 k " +
                 "triangles, inside the 40 k budget in CLAUDE.md.")]
        [Range(32, 256)] public int TerrainMeshResolution = 128;

        [Tooltip("Horizontal resolution of the generated cloud density volume.")]
        [Range(16, 128)] public int CloudVolumeXZ = 64;

        [Tooltip("Vertical resolution of the generated cloud density volume.")]
        [Range(8, 64)] public int CloudVolumeY = 32;

        // ----------------------------------------------------------- performance

        [Header("Performance")]
        [Tooltip("Target display refresh rate on device.")]
        public int TargetFrameRate = 72;

        [Tooltip("Raymarch steps through the cloud volume at full quality.")]
        [Range(8, 96)] public int CloudMarchSteps = 48;

        [Tooltip("Lowest step count the perf governor may fall back to.")]
        [Range(4, 64)] public int CloudMarchStepsMin = 16;

        [Tooltip("Maximum lightning bolts alive at once.")]
        [Range(1, 8)] public int MaxConcurrentBolts = 3;

        // ---------------------------------------------------------------- misc

        [Header("Determinism")]
        [Tooltip("Seed for every procedural fallback, so a demo looks identical " +
                 "across runs and across machines.")]
        public int ProceduralSeed = 20260723;

        [Header("Data")]
        [Tooltip("If true the app tries a live Open-Meteo fetch on start and falls " +
                 "back to baked StreamingAssets data on failure. If false it uses " +
                 "baked data only (recommended for a demo with no Wi-Fi).")]
        public bool AllowLiveFetch = true;

        [Tooltip("Seconds to wait for the live weather request before giving up.")]
        public float LiveFetchTimeoutSeconds = 6f;

        [Tooltip("Demo mode: ignore live and baked weather and always generate the " +
                 "synthetic squall line. Real Shanghai weather is clear most days — a " +
                 "true snapshot is often an empty sky with no lightning at all, which " +
                 "is correct and undemonstrable. The app still labels the result " +
                 "'procedural' on screen, so this shows a storm without claiming one.")]
        public bool ForceProceduralWeather = false;

        // ------------------------------------------------------- derived values

        /// <summary>
        /// Every unit conversion the app performs. Lives in its own struct so it can
        /// be tested without the Unity runtime — see <c>tools/verify.py</c>.
        /// </summary>
        public MapScale Scale => new MapScale(
            MapSizeMeters,
            (float)(RegionSpanKm * 1000.0),
            VerticalExaggeration,
            TerrainReliefExaggeration,
            AtmosphereFloorMeters);

        /// <summary>VR metres per real-world metre, horizontally.</summary>
        public float HorizontalScale => Scale.Horizontal;

        /// <summary>VR metres per real-world metre, vertically (altitude).</summary>
        public float VerticalScale => Scale.Vertical;

        /// <summary>Height of the rendered cloud volume in VR metres.</summary>
        public float AtmosphereHeightVr =>
            (AtmosphereCeilingMeters - AtmosphereFloorMeters) * VerticalScale;

        /// <summary>Converts a real-world altitude in metres to VR metres above the map plane.</summary>
        public float AltitudeToVr(float altitudeMeters) => Scale.AltitudeToVr(altitudeMeters);

        /// <summary>VR metres to the map root's normalised local units.</summary>
        public float MetersToMapUnits(float meters) => Scale.MetersToMapUnits(meters);

        /// <summary>Real-world altitude in metres to map-local Y.</summary>
        public float AltitudeToMapUnits(float altitudeMeters) => Scale.AltitudeToMapUnits(altitudeMeters);

        /// <summary>Terrain elevation in metres to map-local Y, with relief exaggeration.</summary>
        public float TerrainElevationToMapUnits(float elevationMeters) =>
            Scale.TerrainElevationToMapUnits(elevationMeters);

        /// <summary>A building's true height in metres to map-local Y, no exaggeration.</summary>
        public float BuildingHeightToMapUnits(float heightMeters) =>
            Scale.BuildingHeightToMapUnits(heightMeters);

        // ------------------------------------------------------------- singleton

        static AppConfig _instance;

        public static AppConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<AppConfig>(ResourcePath);
                    if (_instance == null)
                    {
                        _instance = CreateInstance<AppConfig>();
                        _instance.name = "WeatherVRConfig (defaults)";
                    }
                }
                return _instance;
            }
        }

        /// <summary>Test/editor hook: force a specific config instance.</summary>
        public static void Override(AppConfig config) => _instance = config;
    }
}

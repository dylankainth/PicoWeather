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

        [Header("Region (Shanghai)")]
        [Tooltip("Latitude of the map centre, degrees north.")]
        public double CenterLatitude = 31.23;

        [Tooltip("Longitude of the map centre, degrees east.")]
        public double CenterLongitude = 121.47;

        [Tooltip("Edge length of the square region covered by the map, in kilometres.")]
        public double RegionSpanKm = 50.0;

        // ----------------------------------------------------------------- scale

        [Header("Tabletop scale")]
        [Tooltip("Edge length of the map in VR metres. 2 m across a 50 km region " +
                 "means 1 VR metre = 25 km.")]
        public float MapSizeMeters = 2.0f;

        [Tooltip("Vertical exaggeration relative to true scale. 1.0 renders altitude " +
                 "at exactly the same scale as horizontal distance (a 2 km cloud sits " +
                 "8 cm above a 2 m map). Values above 1 make the atmosphere read as " +
                 "volumetric rather than as a flat film; the spec's literal '2 km = " +
                 "4 cm' corresponds to 0.5.")]
        public float VerticalExaggeration = 4.0f;

        [Tooltip("Extra vertical exaggeration applied to terrain relief only. Real " +
                 "Shanghai relief is a few tens of metres over 50 km, which is " +
                 "invisible at true scale, so terrain gets its own boost.")]
        public float TerrainReliefExaggeration = 12.0f;

        // ------------------------------------------------------------ atmosphere

        [Header("Atmosphere")]
        [Tooltip("Altitude in metres below which no cloud is rendered.")]
        public float AtmosphereFloorMeters = 200f;

        [Tooltip("Altitude in metres of the top of the rendered cloud volume. " +
                 "12 km covers the high (cirrus) layer.")]
        public float AtmosphereCeilingMeters = 12000f;

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

        // ------------------------------------------------------- derived values

        /// <summary>VR metres per real-world metre, horizontally.</summary>
        public float HorizontalScale => MapSizeMeters / (float)(RegionSpanKm * 1000.0);

        /// <summary>VR metres per real-world metre, vertically (altitude).</summary>
        public float VerticalScale => HorizontalScale * VerticalExaggeration;

        /// <summary>Height of the rendered cloud volume in VR metres.</summary>
        public float AtmosphereHeightVr =>
            (AtmosphereCeilingMeters - AtmosphereFloorMeters) * VerticalScale;

        /// <summary>Converts a real-world altitude in metres to VR metres above the map plane.</summary>
        public float AltitudeToVr(float altitudeMeters) =>
            (altitudeMeters - AtmosphereFloorMeters) * VerticalScale;

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

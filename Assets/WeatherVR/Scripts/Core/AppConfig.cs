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

        [Tooltip("Extra horizontal inflation of every building footprint about its own " +
                 "centroid, on top of the map's scale. 1 = true geographic footprint. " +
                 "Above 1 makes the city read as a model of a city rather than a scatter " +
                 "of chips: at 1:1667 a 40 m-wide block is only 2.4 cm across. Note that " +
                 "the City of London is genuinely dense, so inflating footprints will " +
                 "make neighbouring buildings intersect -- that is the trade being made.")]
        [Range(1f, 3f)] public float BuildingFootprintScale = 1.5f;

        [Tooltip("Extra vertical exaggeration for building height only, on top of the " +
                 "true-scale conversion in MapScale.BuildingHeightToMapUnits. Kept as a " +
                 "separate knob from BuildingFootprintScale so towers can be made to " +
                 "read without widening them, and separate from TerrainReliefExaggeration " +
                 "because buildings start life visible and terrain relief does not. " +
                 "Watch this against the cloud base (WeatherVisuals.CloudBaseMeters): " +
                 "push it far enough and a skyscraper punches through the deck.")]
        [Range(1f, 4f)] public float BuildingHeightExaggeration = 1.5f;

        // ----------------------------------------------------------------- scale

        [Header("Tabletop scale")]
        [Tooltip("Edge length of the map in VR metres. 3 m across a 5 km region means " +
                 "1 VR metre = 1.67 km. Everything under the map root is authored in the " +
                 "normalised [-0.5, 0.5] square and scaled by this, so changing it scales " +
                 "terrain, buildings, clouds, lightning and the plinth together -- see " +
                 "PedestalReferenceMapSizeMeters for the one thing deliberately held back " +
                 "from that, and WeatherCarouselFollower.WallHalfExtent / " +
                 "SceneBuilder.Populate's ComfortFollow distance for the two values that " +
                 "have to be retuned alongside it.")]
        public float MapSizeMeters = 3.0f;

        [Tooltip("The map size the pedestal mesh's ring radii were authored against. " +
                 "PedestalMeshBuilder works in map units, so the plinth would otherwise " +
                 "grow with MapSizeMeters; dividing by that ratio holds it at a fixed " +
                 "size in VR metres instead, which is what keeps it reading as a table " +
                 "you stand at rather than a monument. Consequence, deliberate: once " +
                 "MapSizeMeters exceeds this the terrain's 0.5-unit edge overhangs the " +
                 "plinth's crown, so the heightfield's underside is no longer hidden by " +
                 "the pedestal shader's Cull Front depth pass. Set equal to " +
                 "MapSizeMeters to go back to a plinth that scales with the map.")]
        public float PedestalReferenceMapSizeMeters = 2.0f;

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

        [Header("Kaleidoscope surround")]
        [Tooltip("0 = plain .xyz liquid glass: a single unfolded environment reflection, " +
                 "no facet dispersion, a plain gradient sky and a Cartesian floor grid. " +
                 "1 = full kaleidoscope fold. Continuous, so the accent can be dialled " +
                 "back rather than switched off -- reverting the look never means " +
                 "reverting code. Published by EnvironmentController as the " +
                 "_WVRKaleidoAmount shader global and read by the skybox, the glass " +
                 "floor and the plinth, so all three fold together.")]
        [Range(0f, 1f)] public float PedestalKaleidoscope = 1f;

        [Tooltip("How many mirror wedges the whole surround folds into -- the sky, the " +
                 "floor's radial spokes and the plinth's reflections all share this one " +
                 "count. 8 matches the pedestal's eight physical facets, which is what " +
                 "makes a facet's reflection line up with the sky it is reflecting; the " +
                 "shader rounds it to an integer, since a fractional count leaves one " +
                 "discontinuous seam where the azimuth wraps.")]
        [Range(3f, 16f)] public float KaleidoSegments = 8f;

        [Tooltip("Radians per second the fold rotates. Small on purpose, and note that " +
                 "only the sky and the glass reflections spin -- the floor's structural " +
                 "spokes are deliberately static, because a rotating line across the " +
                 "lower field of view is a vection trigger (see GlassSurround.shader).")]
        [Range(-1f, 1f)] public float KaleidoSpin = 0.06f;

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
            AtmosphereFloorMeters,
            BuildingHeightExaggeration);

        /// <summary>
        /// Multiplier applied to the pedestal mesh's map-unit radii so the plinth keeps
        /// a constant size in VR metres as the map grows. 1 when the map is at the
        /// pedestal's reference size.
        ///
        /// Clamped at 1, so this can only ever hold the plinth back, never inflate it:
        /// a map <em>smaller</em> than the reference should keep the plinth proportional
        /// (which is what the phone AR/touch builds want — they override the map root's
        /// scale to <c>PhoneMapSizeMeters</c> at runtime, and a plinth pinned to a 2 m
        /// reference under a 0.6 m map would be a 2.3 m slab around a phone-sized model).
        /// </summary>
        public float PedestalMapUnitScale => Mathf.Min(
            1f,
            Mathf.Max(PedestalReferenceMapSizeMeters, 1e-4f) / Mathf.Max(MapSizeMeters, 1e-4f));

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

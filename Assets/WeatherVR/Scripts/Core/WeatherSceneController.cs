using System.Collections;
using UnityEngine;
using WeatherVR.Audio;
using WeatherVR.Clouds;
using WeatherVR.Data;
using WeatherVR.Interaction;
using WeatherVR.Lightning;
using WeatherVR.Terrain;

namespace WeatherVR.Core
{
    /// <summary>
    /// Boots the app: load the data, hand it to every renderer, hand control to the
    /// user.
    ///
    /// This is the only place that knows the whole scene, and it deliberately stays
    /// the only place. Every subsystem takes a <see cref="WeatherSnapshot"/> through
    /// an <c>Apply</c> method and knows nothing about the others, which is what
    /// makes them individually testable and what will make a forecast timeline —
    /// replaying <c>Apply</c> with successive snapshots — a small change rather than
    /// a rewrite.
    /// </summary>
    public class WeatherSceneController : MonoBehaviour
    {
        [Header("Scene graph")]
        [Tooltip("Root of everything that moves when the map is placed. Local space is " +
                 "the normalised [-0.5, 0.5] map square scaled to MapSizeMeters.")]
        public Transform MapRoot;

        [Header("Subsystems")]
        public WeatherDataService DataService;
        public TerrainRenderer Terrain;
        public BuildingRenderer Buildings;
        public CloudRenderer Clouds;
        public RainRenderer Rain;
        public LightningDirector Lightning;
        public AmbientSoundscape Soundscape;
        public MapPlacementController Placement;
        public PerfGovernor Governor;

        [Header("Lighting")]
        [Tooltip("Key light standing in for the sun. Its angle is set from the region " +
                 "and the snapshot's observation time.")]
        public Light SunLight;

        [Tooltip("Ambient colour before any lightning flash.")]
        public Color BaseAmbient = new Color(0.30f, 0.34f, 0.40f);

        [Header("Status")]
        [Tooltip("Optional label showing where the data came from. Provenance is not " +
                 "decoration: half of what is on screen is derived rather than observed.")]
        public ProvenanceLabel Provenance;

        /// <summary>The snapshot currently being rendered. Null until loading completes.</summary>
        public WeatherSnapshot Snapshot { get; private set; }

        /// <summary>True once the scene has data and is drawing it.</summary>
        public bool IsReady { get; private set; }

        /// <summary>Raised once the scene is fully built.</summary>
        public event System.Action<WeatherSnapshot> Ready;

        void Awake()
        {
            if (MapRoot == null) MapRoot = transform;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = BaseAmbient;
        }

        IEnumerator Start()
        {
            var config = AppConfig.Instance;

            // The cloud shader clips its march against scene depth, which in Built-in
            // forward means the camera has to be asked for a depth texture. Doing it
            // here rather than in the shader's own setup keeps the cost visible.
            var camera = Camera.main;
            if (camera != null)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth;
                // Re-asserted here so a scene generated before head tracking existed,
                // or one edited by hand, still follows the headset. Silent failure
                // otherwise: stereo renders fine, the view just never moves.
                HeadTracking.Ensure(camera.gameObject);
            }

            ApplyMapScale(config);

            if (DataService == null)
            {
                Debug.LogError("[WeatherVR] No WeatherDataService assigned; nothing to draw.");
                yield break;
            }

            Provenance?.SetStatus("Loading weather data…");

            yield return DataService.Load();

            Snapshot = DataService.Snapshot;
            if (Snapshot == null || !Snapshot.IsComplete)
            {
                Debug.LogError("[WeatherVR] Data service returned an incomplete snapshot.");
                Provenance?.SetStatus("Weather data unavailable");
                yield break;
            }

            Build(Snapshot, config);

            IsReady = true;
            Ready?.Invoke(Snapshot);
        }

        void ApplyMapScale(AppConfig config)
        {
            // Everything under the map root works in a normalised unit square, so the
            // root's scale is the single place the tabletop size is expressed.
            MapRoot.localScale = Vector3.one * config.MapSizeMeters;
        }

        /// <summary>
        /// Pushes a snapshot into every subsystem. Safe to call again with a new
        /// snapshot — this is the hook a forecast timeline would use.
        /// </summary>
        public void Build(WeatherSnapshot snapshot, AppConfig config)
        {
            ApplyMapScale(config);
            ApplySunLight(snapshot, config);

            Terrain?.Apply(snapshot, config);
            Buildings?.Apply(snapshot, config);
            Clouds?.Apply(snapshot, config);
            Rain?.Apply(snapshot, config);
            Lightning?.Apply(snapshot, config, MapRoot);
            Soundscape?.Apply(snapshot);

            if (Provenance != null)
            {
                Provenance.SetSnapshot(snapshot, config);
                Provenance.Governor = Governor;
                Provenance.Lightning = Lightning;
            }

            Debug.Log($"[WeatherVR] Scene built — {snapshot.Describe()}");
        }

        /// <summary>
        /// Points the key light where the sun actually was.
        ///
        /// A solar position good to a degree or so is easy and worth having: it is
        /// what makes a mid-afternoon snapshot of Shanghai light the cloud tops from
        /// the west rather than from wherever the light happened to be dropped.
        /// </summary>
        void ApplySunLight(WeatherSnapshot snapshot, AppConfig config)
        {
            if (SunLight == null) return;

            System.DateTime observationUtc = System.DateTime.UtcNow;
            if (!string.IsNullOrEmpty(snapshot.Weather?.observationTimeUtc))
            {
                System.DateTime.TryParse(
                    snapshot.Weather.observationTimeUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out observationUtc);
            }

            SolarPosition.Compute(observationUtc, config.CenterLatitude, config.CenterLongitude,
                                  out double elevationDegrees, out double azimuthDegrees);

            // Below the horizon: keep a low, dim, blue key rather than going black,
            // because a completely unlit map is a worse demo than a slightly wrong one.
            bool daylight = elevationDegrees > 0.0;
            float elevation = daylight ? (float)elevationDegrees : 4f;

            // Unity's yaw is clockwise from +Z (north), which is also how solar azimuth
            // is conventionally measured, so the azimuth maps across directly.
            SunLight.transform.rotation = Quaternion.Euler(elevation, (float)azimuthDegrees + 180f, 0f);

            float warmth = Mathf.InverseLerp(0f, 35f, elevation);
            SunLight.color = daylight
                ? Color.Lerp(new Color(1f, 0.72f, 0.52f), new Color(1f, 0.97f, 0.92f), warmth)
                : new Color(0.55f, 0.62f, 0.85f);
            SunLight.intensity = daylight ? Mathf.Lerp(0.8f, 1.6f, warmth) : 0.25f;
            SunLight.shadows = LightShadows.None;

            Debug.Log($"[WeatherVR] Sun at {elevationDegrees:F1}° elevation, " +
                      $"{azimuthDegrees:F1}° azimuth for {observationUtc:u}.");
        }
    }
}

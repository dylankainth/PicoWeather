using UnityEngine;
using WeatherVR.Clouds;
using WeatherVR.Core;
using WeatherVR.Data;
using WeatherVR.Lightning;

namespace WeatherVR.Weather
{
    /// <summary>
    /// Turns a chosen weather look into a fully rendered scene.
    ///
    /// It reuses the app's existing renderers rather than owning its own: it
    /// synthesises a <see cref="WeatherDataset"/> for the profile and hands it to the
    /// same cloud/rain/lightning <c>Apply</c> methods the initial load uses, then sets
    /// the sun, ambient, fog and the glass surround. Terrain and buildings are left
    /// untouched — they do not change with the weather, so re-meshing them on every
    /// card tap would be pure waste.
    ///
    /// Switching is therefore the same operation the code always described as "the
    /// hook a forecast timeline would use": replay Apply with a new snapshot.
    /// </summary>
    public sealed class WeatherSceneDirector : MonoBehaviour
    {
        [Header("Renderers (weather only — terrain/buildings stay put)")]
        public CloudRenderer Clouds;
        public RainRenderer Rain;
        public LightningDirector Lightning;

        [Header("Lighting & surround")]
        public Light Sun;
        public EnvironmentController Environment;

        [Tooltip("Map root the lightning bolt pool parents under.")]
        public Transform MapRoot;

        [Tooltip("Grid resolution of the synthesised scene weather. 24 is smooth and cheap.")]
        [Range(8, 48)] public int SceneGrid = 24;

        WeatherSnapshot _base;
        AppConfig _config;
        WeatherSceneKind _current = WeatherSceneKind.Clear;
        bool _hasApplied;

        /// <summary>The scene currently being rendered.</summary>
        public WeatherSceneKind Current => _current;

        /// <summary>
        /// Gives the director the loaded terrain/imagery/bounds it reuses for every
        /// synthesised scene. Called once, after the initial data load.
        /// </summary>
        public void Initialize(WeatherSnapshot baseSnapshot, AppConfig config)
        {
            _base = baseSnapshot;
            _config = config;
        }

        /// <summary>Switches to a weather look using that scene's own glass palette.</summary>
        public void ApplyKind(WeatherSceneKind kind)
        {
            WeatherSceneProfile p = WeatherScene.Default(kind);
            ApplyKind(kind, p.SunGlow, p.GlassInner);
        }

        /// <summary>
        /// Switches to a weather look, grading the whole scene toward the tapped
        /// card's accent and glass tint so the terrain and the UI visibly agree.
        /// </summary>
        public void ApplyKind(WeatherSceneKind kind, Color accent, Color glassTint)
        {
            if (_base == null || _config == null)
            {
                Debug.LogWarning("[WeatherVR] WeatherSceneDirector.ApplyKind before Initialize; ignored.");
                return;
            }

            _current = kind;
            _hasApplied = true;

            WeatherSceneProfile profile = WeatherScene.Default(kind).TintWith(accent, glassTint);

            WeatherDataset weather = WeatherScene.Synthesize(
                profile, _base.Bounds, SceneGrid, _config.ProceduralSeed + (int)kind * 17 + 1);

            var snapshot = new WeatherSnapshot
            {
                Bounds = _base.Bounds,
                Terrain = _base.Terrain,
                Satellite = _base.Satellite,
                Buildings = _base.Buildings,
                Weather = weather,
                TerrainSource = _base.TerrainSource,
                SatelliteSource = _base.SatelliteSource,
                BuildingsSource = _base.BuildingsSource,
                WeatherSource = "scene (" + kind + ")"
            };

            Clouds?.Apply(snapshot, _config);
            Rain?.Apply(snapshot, _config);
            Lightning?.Apply(snapshot, _config, MapRoot != null ? MapRoot : transform);

            Vector3 sunDirection = ApplySun(profile);
            ApplyAtmosphere(profile);
            Environment?.Apply(profile, sunDirection);

            Debug.Log($"[WeatherVR] Weather scene → {kind} " +
                      $"(cover {profile.CloudCover:F2}, precip {profile.PrecipMmHr:F1} mm/h).");
        }

        /// <summary>Points and colours the key light for the scene. Returns the direction toward the sun.</summary>
        Vector3 ApplySun(WeatherSceneProfile profile)
        {
            Vector3 towardSun = Vector3.up;
            if (Sun != null)
            {
                Sun.transform.rotation = Quaternion.Euler(profile.SunElevation, profile.SunAzimuth, 0f);
                Sun.color = profile.SunColor;
                Sun.intensity = profile.SunIntensity;
                Sun.shadows = LightShadows.None;
                // A directional light's forward is the direction light travels, so the
                // direction toward the sun is the opposite.
                towardSun = -Sun.transform.forward;
            }
            return towardSun;
        }

        void ApplyAtmosphere(WeatherSceneProfile profile)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = profile.Ambient;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = profile.FogColor;
            RenderSettings.fogDensity = profile.FogDensity;
        }
    }
}

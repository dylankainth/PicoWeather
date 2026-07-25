using UnityEngine;
using WeatherVR.Core;

namespace WeatherVR.Weather
{
    /// <summary>
    /// Turns a chosen weather case into a rendered scene: it drives the new
    /// <see cref="WeatherVisuals"/> (clouds, precipitation, lightning), points and
    /// colours the sun, and sets the ambient/fog and the glass sky through
    /// <see cref="EnvironmentController"/>.
    ///
    /// This is the clean rebuild — it does not touch the old volumetric cloud /
    /// particle rain / lightning-bolt renderers at all. The terrain and buildings are
    /// left alone; they do not change with the weather.
    /// </summary>
    public sealed class WeatherSceneDirector : MonoBehaviour
    {
        public WeatherVisuals Visuals;
        public Light Sun;
        public EnvironmentController Environment;

        /// <summary>Sunrise/sunset are fixed at 06:00/18:00 with solar noon at 12:00 --
        /// a clean 24 h sinusoid rather than a real solar position for London's date.
        /// The slider is a demonstration of "what does this place look like at 03:00",
        /// not an ephemeris, and an exactly periodic curve means dragging across
        /// midnight cannot step.</summary>
        const float SolarNoonHour = 12f;

        static readonly Color SunriseColor = new Color(1f, 0.60f, 0.34f);
        static readonly Color MoonColor = new Color(0.40f, 0.50f, 0.78f);

        WeatherSceneKind _current = WeatherSceneKind.Clear;
        float _hour = 12f;
        bool _ready;

        public WeatherSceneKind Current => _current;

        /// <summary>Hour of day the scene is lit for, 0..24.</summary>
        public float Hour => _hour;

        /// <summary>
        /// Signed solar elevation, -1 at midnight through 0 at sunrise to +1 at noon.
        /// </summary>
        float SolarElevation =>
            Mathf.Sin(Mathf.PI * 2f * (_hour - (SolarNoonHour - 6f)) / 24f);

        /// <summary>
        /// How lit the world is, 0..1. Reaches 0 well before the sun is fully down
        /// (hence the offset) so late evening is dim rather than snapping to black at
        /// the exact moment the sun crosses the horizon.
        /// </summary>
        float Daylight => Mathf.Clamp01(SolarElevation * 3f + 0.25f);

        /// <summary>Builds the visuals once. Safe to call more than once.</summary>
        public void Initialize(AppConfig config)
        {
            if (Visuals != null) Visuals.Build(config);
            _ready = true;
        }

        /// <summary>Switches the whole scene to a weather case.</summary>
        public void ApplyKind(WeatherSceneKind kind)
        {
            if (!_ready)
            {
                Debug.LogWarning("[WeatherVR] WeatherSceneDirector.ApplyKind before Initialize; ignored.");
                return;
            }

            _current = kind;
            WeatherSceneProfile profile = WeatherScene.Default(kind);

            // The expensive half: Show rebuilds the cloud density volume. Everything
            // else lives in RefreshLighting, which the time slider can call freely.
            Visuals?.Show(profile);
            RefreshLighting(profile);

            Debug.Log($"[WeatherVR] Weather scene → {profile.DisplayName} " +
                      $"at {_hour:00.0}h " +
                      $"(cloud {profile.CloudAmount:F2}, precip {profile.Precip}/{profile.PrecipIntensity:F2}, " +
                      $"lightning {profile.Lightning}).");
        }

        /// <summary>
        /// Moves the time of day without rebuilding anything.
        ///
        /// This is the time slider's per-frame path, and the split is the whole point:
        /// dragging the slider re-points and re-colours the sun, re-grades the sky and
        /// the fog — all cheap — while <see cref="ApplyKind"/>'s volume rebuild only
        /// runs when the scrub actually crosses into a different weather case.
        /// </summary>
        public void SetTimeOfDay(float hour)
        {
            _hour = Mathf.Repeat(hour, 24f);
            if (!_ready) return;
            RefreshLighting(WeatherScene.Default(_current));
        }

        void RefreshLighting(WeatherSceneProfile profile)
        {
            Vector3 sunDirection = ApplySun(profile);
            ApplyAtmosphere(profile);
            Environment?.Apply(profile, sunDirection, Daylight);
        }

        Vector3 ApplySun(WeatherSceneProfile profile)
        {
            Vector3 towardSun = Vector3.up;
            if (Sun == null)
                return towardSun;

            float elevationNorm = SolarElevation;
            float daylight = Daylight;

            // The profile's SunElevation becomes the *peak* elevation for that
            // weather, so an overcast day still keeps its lower, duller sun while
            // following the same arc. Its SunAzimuth is deliberately ignored: with a
            // 24 h slider the azimuth has to come from the clock, or the sun would
            // rise and set in the same spot.
            float elevation = profile.SunElevation * elevationNorm;
            float azimuth = 180f + (_hour - SolarNoonHour) * 15f;

            // Warm the light as it nears the horizon, cool it once it is below --
            // between them, the two cues that make an hour readable at a glance.
            float warmth = Mathf.Clamp01(1f - Mathf.Abs(elevationNorm) * 2f);
            Color lit = Color.Lerp(profile.SunColor, SunriseColor, warmth * 0.75f);

            Sun.transform.rotation = Quaternion.Euler(elevation, azimuth, 0f);
            Sun.color = Color.Lerp(MoonColor, lit, daylight);
            // Never fully dark: a moonlit floor keeps the terrain and buildings
            // readable at 03:00, which a demo needs even where physics would not.
            Sun.intensity = Mathf.Max(profile.SunIntensity * daylight, 0.06f);
            Sun.shadows = LightShadows.None;
            return -Sun.transform.forward;
        }

        void ApplyAtmosphere(WeatherSceneProfile profile)
        {
            float dim = Mathf.Lerp(0.16f, 1f, Daylight);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = profile.Ambient * dim;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = profile.FogColor * dim;
            RenderSettings.fogDensity = profile.FogDensity;
        }
    }
}

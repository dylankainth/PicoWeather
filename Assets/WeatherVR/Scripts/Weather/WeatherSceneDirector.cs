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

        WeatherSceneKind _current = WeatherSceneKind.Clear;
        bool _ready;

        public WeatherSceneKind Current => _current;

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

            Visuals?.Show(profile);
            Vector3 sunDirection = ApplySun(profile);
            ApplyAtmosphere(profile);
            Environment?.Apply(profile, sunDirection);

            Debug.Log($"[WeatherVR] Weather scene → {profile.DisplayName} " +
                      $"(cloud {profile.CloudAmount:F2}, precip {profile.Precip}/{profile.PrecipIntensity:F2}, " +
                      $"lightning {profile.Lightning}).");
        }

        Vector3 ApplySun(WeatherSceneProfile profile)
        {
            Vector3 towardSun = Vector3.up;
            if (Sun != null)
            {
                Sun.transform.rotation = Quaternion.Euler(profile.SunElevation, profile.SunAzimuth, 0f);
                Sun.color = profile.SunColor;
                Sun.intensity = profile.SunIntensity;
                Sun.shadows = LightShadows.None;
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

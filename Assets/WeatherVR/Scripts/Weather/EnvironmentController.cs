using UnityEngine;

namespace WeatherVR.Weather
{
    /// <summary>
    /// Owns the studio-sky surround. It guarantees a sky material exists at runtime
    /// (a null skybox falls back to the camera's flat clear colour — the old black),
    /// and re-tints its gradient and sun glow per weather scene so the blue around
    /// the tabletop shifts with the weather: bright for clear, grey for overcast,
    /// dark violet for a thunderstorm.
    ///
    /// The earlier glass floor disc was removed on request — the surround is now just
    /// the table and the sky.
    /// </summary>
    public sealed class EnvironmentController : MonoBehaviour
    {
        Material _skyMaterial;

        static readonly int ZenithId = Shader.PropertyToID("_Zenith");
        static readonly int HorizonId = Shader.PropertyToID("_Horizon");
        static readonly int NadirId = Shader.PropertyToID("_Nadir");
        static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        static readonly int SunGlowId = Shader.PropertyToID("_SunGlow");
        static readonly int SunDirId = Shader.PropertyToID("_SunDir");
        static readonly int PrismColorId = Shader.PropertyToID("_PrismColor");
        static readonly int PrismStrengthId = Shader.PropertyToID("_PrismStrength");
        static readonly int LatticeStrengthId = Shader.PropertyToID("_LatticeStrength");

        void Awake()
        {
            EnsureSky();
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

        /// <summary>Re-tints the sky gradient and sun glow for a weather scene.</summary>
        public void Apply(WeatherSceneProfile profile, Vector3 sunDirection)
        {
            if (_skyMaterial == null) _skyMaterial = RenderSettings.skybox;
            if (_skyMaterial == null) return;

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
            _skyMaterial.SetColor(
                ZenithId,
                Color.Lerp(zenith, profile.SunGlow, 0.08f));
            _skyMaterial.SetColor(
                HorizonId,
                Color.Lerp(horizon, profile.SunGlow, 0.11f));
            _skyMaterial.SetColor(
                NadirId,
                nadir);
            _skyMaterial.SetColor(SunColorId, profile.SunGlow);
            _skyMaterial.SetFloat(SunGlowId, profile.SunGlowStrength * 0.62f);
            _skyMaterial.SetVector(SunDirId, sunDirection);

            // Keep the surround weather-reactive, but within a narrow, comfortable
            // range so changing cards never produces an abrupt peripheral flash.
            Color violet = new Color(0.52f, 0.26f, 0.57f);
            Color prism = Color.Lerp(violet, profile.SunGlow, 0.18f);
            _skyMaterial.SetColor(PrismColorId, prism);
            _skyMaterial.SetFloat(
                PrismStrengthId,
                Mathf.Lerp(0.22f, 0.29f, profile.CloudDarkness));
            _skyMaterial.SetFloat(
                LatticeStrengthId,
                Mathf.Lerp(0.055f, 0.075f, profile.CloudDarkness));
        }
    }
}

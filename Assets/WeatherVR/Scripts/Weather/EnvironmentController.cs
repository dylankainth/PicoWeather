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

            Color neutral = new Color(0.18f, 0.24f, 0.23f);
            _skyMaterial.SetColor(
                ZenithId,
                Color.Lerp(profile.SkyZenith, neutral, 0.60f));
            _skyMaterial.SetColor(
                HorizonId,
                Color.Lerp(profile.SkyHorizon, neutral, 0.58f));
            _skyMaterial.SetColor(
                NadirId,
                Color.Lerp(profile.SkyNadir, neutral, 0.68f));
            _skyMaterial.SetColor(SunColorId, profile.SunGlow);
            _skyMaterial.SetFloat(SunGlowId, profile.SunGlowStrength * 0.62f);
            _skyMaterial.SetVector(SunDirId, sunDirection);

            // Keep the surround weather-reactive, but within a narrow, comfortable
            // range so changing cards never produces an abrupt peripheral flash.
            Color jade = new Color(0.28f, 0.67f, 0.64f);
            Color prism = Color.Lerp(jade, profile.SunGlow, 0.28f);
            _skyMaterial.SetColor(PrismColorId, prism);
            _skyMaterial.SetFloat(
                PrismStrengthId,
                Mathf.Lerp(0.13f, 0.17f, profile.CloudDarkness));
            _skyMaterial.SetFloat(
                LatticeStrengthId,
                Mathf.Lerp(0.035f, 0.050f, profile.CloudDarkness));
        }
    }
}

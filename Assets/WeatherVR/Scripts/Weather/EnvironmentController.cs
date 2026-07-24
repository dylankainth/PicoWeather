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

            _skyMaterial.SetColor(ZenithId, profile.SkyZenith);
            _skyMaterial.SetColor(HorizonId, profile.SkyHorizon);
            _skyMaterial.SetColor(NadirId, profile.SkyNadir);
            _skyMaterial.SetColor(SunColorId, profile.SunGlow);
            _skyMaterial.SetFloat(SunGlowId, profile.SunGlowStrength);
            _skyMaterial.SetVector(SunDirId, sunDirection);
        }
    }
}

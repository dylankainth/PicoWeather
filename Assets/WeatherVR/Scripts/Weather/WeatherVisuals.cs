using UnityEngine;
using WeatherVR.Core;

namespace WeatherVR.Weather
{
    /// <summary>
    /// The app's weather renderer, built from scratch — it does not use the old
    /// volumetric cloud / particle-rain / lightning-bolt code as a base.
    ///
    /// Everything lives under the map root, in the normalised [-0.5, 0.5] map square,
    /// so it sits above the terrain and travels with the table. It is driven entirely
    /// by a <see cref="WeatherSceneProfile"/>:
    ///   • a soft billboard cloud deck whose amount/darkness/drift come from the profile,
    ///   • a precipitation system that falls as rain streaks or snow flakes,
    ///   • a pulsing lightning flash (light + a jagged bolt) for the thunderstorm.
    /// Fog and sky are handled by the environment/director; this owns the moving parts.
    /// </summary>
    public sealed class WeatherVisuals : MonoBehaviour
    {
        const float CloudBaseMeters = 900f;
        const float CloudThicknessMeters = 700f;

        AppConfig _config;
        bool _built;

        ParticleSystem _clouds;
        ParticleSystemRenderer _cloudsRenderer;
        ParticleSystem _precip;
        ParticleSystemRenderer _precipRenderer;

        Material _cloudMaterial, _rainMaterial, _snowMaterial, _boltMaterial;
        Texture2D _softCircle, _streak;

        Light _flashLight;
        LineRenderer _bolt;

        bool _lightningEnabled;
        float _nextFlashTime;
        float _flashEnergy;
        System.Random _rng;

        float _cloudBaseMap, _cloudTopMap;

        public void Build(AppConfig config)
        {
            if (_built) return;
            _built = true;
            _config = config;
            _rng = new System.Random(config.ProceduralSeed ^ 0x51A7);

            _cloudBaseMap = config.AltitudeToMapUnits(CloudBaseMeters);
            _cloudTopMap = config.AltitudeToMapUnits(CloudBaseMeters + CloudThicknessMeters);

            BuildTextures();
            BuildClouds();
            BuildPrecip();
            BuildLightning();
        }

        // --------------------------------------------------------------- build

        void BuildTextures()
        {
            _softCircle = SoftCircle(64);
            _streak = Streak(16, 64);

            var sprite = Shader.Find("Sprites/Default");
            _cloudMaterial = new Material(sprite) { name = "Cloud (runtime)", mainTexture = _softCircle };
            _rainMaterial = new Material(sprite) { name = "Rain (runtime)", mainTexture = _streak };
            _snowMaterial = new Material(sprite) { name = "Snow (runtime)", mainTexture = _softCircle };
            _boltMaterial = new Material(sprite) { name = "Bolt (runtime)" };
        }

        void BuildClouds()
        {
            var go = new GameObject("CloudDeck");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, (_cloudBaseMap + _cloudTopMap) * 0.5f, 0f);

            _clouds = go.AddComponent<ParticleSystem>();
            _clouds.Stop();
            _cloudsRenderer = go.GetComponent<ParticleSystemRenderer>();
            _cloudsRenderer.sharedMaterial = _cloudMaterial;
            _cloudsRenderer.renderMode = ParticleSystemRenderMode.Billboard;
            _cloudsRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cloudsRenderer.receiveShadows = false;
            _cloudsRenderer.sortingOrder = 0;

            var main = _clouds.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = 14f;
            main.startSpeed = 0f;
            main.startSize = 0.5f;
            main.maxParticles = 700;
            main.gravityModifier = 0f;

            var shape = _clouds.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(1.05f, Mathf.Max(0.001f, _cloudTopMap - _cloudBaseMap), 1.05f);

            var emission = _clouds.emission;
            emission.enabled = true;

            var vel = _clouds.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
        }

        void BuildPrecip()
        {
            var go = new GameObject("Precipitation");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, _cloudBaseMap, 0f);

            _precip = go.AddComponent<ParticleSystem>();
            _precip.Stop();
            _precipRenderer = go.GetComponent<ParticleSystemRenderer>();
            _precipRenderer.sharedMaterial = _rainMaterial;
            _precipRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _precipRenderer.receiveShadows = false;

            var main = _precip.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.loop = true;
            main.playOnAwake = false;
            main.gravityModifier = 0f;
            main.startSpeed = 0f;
            main.maxParticles = 4000;

            var shape = _precip.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(1.05f, 0.02f, 1.05f);

            var emission = _precip.emission;
            emission.enabled = true;

            var vel = _precip.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
        }

        void BuildLightning()
        {
            var lightGo = new GameObject("LightningFlash");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0f, _cloudBaseMap, 0f);
            _flashLight = lightGo.AddComponent<Light>();
            _flashLight.type = LightType.Point;
            _flashLight.color = new Color(0.8f, 0.85f, 1f);
            _flashLight.range = _config.MapSizeMeters * 2.5f;
            _flashLight.intensity = 0f;
            _flashLight.shadows = LightShadows.None;

            var boltGo = new GameObject("Bolt");
            boltGo.transform.SetParent(transform, false);
            _bolt = boltGo.AddComponent<LineRenderer>();
            _bolt.useWorldSpace = false;
            _bolt.widthMultiplier = 0.006f;
            _bolt.numCapVertices = 1;
            _bolt.sharedMaterial = _boltMaterial;
            _bolt.textureMode = LineTextureMode.Stretch;
            _bolt.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _bolt.receiveShadows = false;
            var white = new Color(0.9f, 0.94f, 1f);
            _bolt.startColor = white;
            _bolt.endColor = white;
            _bolt.enabled = false;
        }

        // ---------------------------------------------------------------- drive

        /// <summary>Reconfigures every effect for a weather profile.</summary>
        public void Show(WeatherSceneProfile p)
        {
            if (!_built) return;

            ShowClouds(p);
            ShowPrecip(p);

            _lightningEnabled = p.Lightning;
            if (!_lightningEnabled)
            {
                _flashLight.intensity = 0f;
                _flashEnergy = 0f;
                _bolt.enabled = false;
            }
            else
            {
                _nextFlashTime = Time.time + 0.6f;
            }
        }

        void ShowClouds(WeatherSceneProfile p)
        {
            float amount = Mathf.Clamp01(p.CloudAmount);

            var emission = _clouds.emission;
            emission.rateOverTime = Mathf.Lerp(0f, 55f, amount);

            var main = _clouds.main;
            // Bright cumulus white grading to storm grey.
            Color bright = new Color(0.98f, 0.98f, 1f);
            Color dark = new Color(0.34f, 0.36f, 0.42f);
            Color c = Color.Lerp(bright, dark, Mathf.Clamp01(p.CloudDarkness));
            c.a = Mathf.Lerp(0.32f, 0.62f, amount);
            main.startColor = c;
            main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.7f);

            // Drift with the wind, exaggerated so it is perceptible at 1:2500.
            var vel = _clouds.velocityOverLifetime;
            float drift = p.WindMs * 0.004f;
            vel.x = new ParticleSystem.MinMaxCurve(drift);
            vel.z = new ParticleSystem.MinMaxCurve(drift * 0.5f);

            if (amount <= 0.001f) _clouds.Stop(); else if (!_clouds.isPlaying) _clouds.Play();
            _clouds.Clear();
        }

        void ShowPrecip(WeatherSceneProfile p)
        {
            if (p.Precip == PrecipKind.None || p.PrecipIntensity <= 0.001f)
            {
                _precip.Stop();
                _precip.Clear();
                return;
            }

            bool snow = p.Precip == PrecipKind.Snow;
            _precipRenderer.sharedMaterial = snow ? _snowMaterial : _rainMaterial;
            _precipRenderer.renderMode = snow
                ? ParticleSystemRenderMode.Billboard
                : ParticleSystemRenderMode.Stretch;
            if (!snow)
            {
                _precipRenderer.lengthScale = 2.5f;
                _precipRenderer.velocityScale = 0.12f;
            }

            float fallSeconds = snow ? 3.5f : 1.0f;
            float fall = _cloudBaseMap / Mathf.Max(fallSeconds, 0.05f);

            var main = _precip.main;
            main.startLifetime = fallSeconds;
            main.startColor = snow
                ? new Color(1f, 1f, 1f, 0.9f)
                : new Color(0.78f, 0.86f, 1f, 0.6f);
            main.startSize = snow
                ? new ParticleSystem.MinMaxCurve(0.006f, 0.012f)
                : new ParticleSystem.MinMaxCurve(0.004f, 0.008f);

            var vel = _precip.velocityOverLifetime;
            vel.y = new ParticleSystem.MinMaxCurve(-fall);
            float sideDrift = p.WindMs * (snow ? 0.01f : 0.02f);
            vel.x = new ParticleSystem.MinMaxCurve(sideDrift);

            var emission = _precip.emission;
            float maxRate = snow ? 700f : 2200f;
            emission.rateOverTime = Mathf.Lerp(0f, maxRate, Mathf.Clamp01(p.PrecipIntensity));

            main.maxParticles = Mathf.CeilToInt(maxRate * fallSeconds) + 128;

            if (!_precip.isPlaying) _precip.Play();
            _precip.Clear();
        }

        void Update()
        {
            if (!_built || !_lightningEnabled) return;

            // Poisson-ish flashes with a quick multi-stroke flicker and decay.
            if (Time.time >= _nextFlashTime)
            {
                _flashEnergy = 1f;
                float u = Mathf.Clamp((float)_rng.NextDouble(), 1e-3f, 1f);
                _nextFlashTime = Time.time + Mathf.Lerp(1.5f, 5.5f, -Mathf.Log(u) * 0.4f);
                StrikeBolt();
            }

            _flashEnergy = Mathf.Max(0f, _flashEnergy - Time.deltaTime * 3.2f);
            // Flicker while decaying so it reads as a real multi-stroke flash.
            float flicker = _flashEnergy * (0.6f + 0.4f * Mathf.Sin(Time.time * 60f));
            _flashLight.intensity = flicker * 6f;

            if (_bolt.enabled)
            {
                var c = _bolt.startColor;
                c.a = Mathf.Clamp01(_flashEnergy * 2f);
                _bolt.startColor = c;
                _bolt.endColor = c;
                if (_flashEnergy <= 0.02f) _bolt.enabled = false;
            }
        }

        void StrikeBolt()
        {
            // A jagged channel from a random point in the cloud base down to the ground,
            // in map-local units.
            float x = ((float)_rng.NextDouble() - 0.5f) * 0.7f;
            float z = ((float)_rng.NextDouble() - 0.5f) * 0.7f;
            _flashLight.transform.localPosition = new Vector3(x, _cloudBaseMap, z);

            const int segments = 7;
            _bolt.positionCount = segments + 1;
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float jitterX = i == 0 || i == segments ? 0f : ((float)_rng.NextDouble() - 0.5f) * 0.05f;
                float jitterZ = i == 0 || i == segments ? 0f : ((float)_rng.NextDouble() - 0.5f) * 0.05f;
                _bolt.SetPosition(i, new Vector3(
                    x + jitterX,
                    Mathf.Lerp(_cloudBaseMap, 0f, t),
                    z + jitterZ));
            }
            _bolt.enabled = true;
        }

        // ------------------------------------------------------------ textures

        static Texture2D SoftCircle(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "SoftCircle" };
            tex.wrapMode = TextureWrapMode.Clamp;
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - r) / r;
                float dy = (y + 0.5f - r) / r;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a); // smoothstep
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            return tex;
        }

        static Texture2D Streak(int w, int h)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "Streak" };
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = Mathf.Abs((x + 0.5f) / w - 0.5f) * 2f;
                float a = Mathf.Clamp01(1f - dx);
                a *= a;
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            return tex;
        }

        void OnDestroy()
        {
            if (_cloudMaterial != null) Destroy(_cloudMaterial);
            if (_rainMaterial != null) Destroy(_rainMaterial);
            if (_snowMaterial != null) Destroy(_snowMaterial);
            if (_boltMaterial != null) Destroy(_boltMaterial);
            if (_softCircle != null) Destroy(_softCircle);
            if (_streak != null) Destroy(_streak);
        }
    }
}

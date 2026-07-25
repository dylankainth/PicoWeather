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
        // Real altitudes in metres, converted through AppConfig.AltitudeToMapUnits, so
        // they track the map's scale on their own. The base was 900 m; it was raised to
        // open up the clear-air gap between the deck and the city -- 1250 m puts the base
        // 25 cm above the tabletop on a 3 m map, half again the ~17 cm the map's own
        // growth already gave. The deck's thickness is unchanged, so the ceiling moves up
        // with the base and still sits well inside AtmosphereCeilingMeters (3500 m).
        //
        // The constraint on raising buildings, not on raising this: with
        // BuildingHeightExaggeration at 1.5 the region's tallest tower (310 m in the
        // current bake) reaches ~28 cm and so pokes ~3 cm into the deck's underside. That
        // is pre-existing and roughly proportional -- at the previous 2 m map and true
        // building scale it poked in by ~1 cm -- but it is the number to watch if either
        // knob moves again.
        const float CloudBaseMeters = 1250f;
        const float CloudThicknessMeters = 700f;

        AppConfig _config;
        bool _built;

        ParticleSystem _clouds;
        ParticleSystemRenderer _cloudsRenderer;
        ParticleSystem _precip;
        ParticleSystemRenderer _precipRenderer;

        Material _cloudMaterial, _rainMaterial, _snowMaterial;
        Material _boltMaterial;
        Texture2D _softCircle, _cloudTexture, _rainStreak, _snowflake;

        Light _flashLight;
        LineRenderer _bolt;

        bool _lightningEnabled;
        float _nextFlashTime;
        float _flashEnergy;
        System.Random _rng;

        float _cloudBaseMap, _cloudTopMap;

        /// <summary>
        /// Fully resolved, scale-independent precipitation settings. Keeping this
        /// calculation separate from the particle-system mutation makes the visual
        /// scale testable without entering Play mode and prevents slider changes from
        /// accumulating stale module state.
        /// </summary>
        public readonly struct PrecipitationStyle
        {
            public readonly bool Enabled;
            public readonly bool Snow;
            public readonly float MinSize;
            public readonly float MaxSize;
            public readonly float MinLifetime;
            public readonly float MaxLifetime;
            public readonly float MinFallSpeed;
            public readonly float MaxFallSpeed;
            public readonly float EmissionRate;
            public readonly float WindDrift;

            public PrecipitationStyle(
                bool enabled,
                bool snow,
                float minSize,
                float maxSize,
                float minLifetime,
                float maxLifetime,
                float minFallSpeed,
                float maxFallSpeed,
                float emissionRate,
                float windDrift)
            {
                Enabled = enabled;
                Snow = snow;
                MinSize = minSize;
                MaxSize = maxSize;
                MinLifetime = minLifetime;
                MaxLifetime = maxLifetime;
                MinFallSpeed = minFallSpeed;
                MaxFallSpeed = maxFallSpeed;
                EmissionRate = emissionRate;
                WindDrift = windDrift;
            }
        }

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
            _cloudTexture = SoftCloud(96, _config.ProceduralSeed ^ 0xC10D);
            _rainStreak = RainStreak(32, 128);
            _snowflake = Snowflake(64);

            var sprite = Shader.Find("Sprites/Default");
            _cloudMaterial = new Material(sprite)
            {
                name = "Cloud (runtime)",
                mainTexture = _cloudTexture
            };
            _rainMaterial = new Material(sprite)
                { name = "Rain (runtime)", mainTexture = _rainStreak };
            _snowMaterial = new Material(sprite)
                { name = "Snow (runtime)", mainTexture = _snowflake };
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
            _cloudsRenderer.alignment = ParticleSystemRenderSpace.View;
            _cloudsRenderer.sortMode = ParticleSystemSortMode.Distance;
            _cloudsRenderer.normalDirection = 0.65f;
            _cloudsRenderer.minParticleSize = 0.015f;
            _cloudsRenderer.maxParticleSize = 0.22f;
            _cloudsRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _cloudsRenderer.receiveShadows = false;
            _cloudsRenderer.sortingOrder = 0;

            var main = _clouds.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.loop = true;
            main.playOnAwake = false;
            main.startLifetime = 14f;
            main.startSpeed = 0f;
            main.startSize = 0.32f;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.maxParticles = 700;
            main.gravityModifier = 0f;

            var shape = _clouds.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(0.82f, Mathf.Max(0.001f, _cloudTopMap - _cloudBaseMap), 0.82f);

            var emission = _clouds.emission;
            emission.enabled = true;

            var vel = _clouds.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;

            // Slow coherent turbulence keeps the deck from reading as a flat sheet
            // of identical sprites. The values are intentionally gentle for VR.
            var noise = _clouds.noise;
            noise.enabled = true;
            noise.strength = 0.035f;
            noise.frequency = 0.32f;
            noise.scrollSpeed = 0.08f;
            noise.damping = true;
            noise.quality = ParticleSystemNoiseQuality.Medium;
        }

        void BuildPrecip()
        {
            var fallingObject = new GameObject("Precipitation");
            fallingObject.transform.SetParent(transform, false);
            fallingObject.transform.localPosition = new Vector3(0f, _cloudBaseMap, 0f);

            _precip = fallingObject.AddComponent<ParticleSystem>();
            _precip.Stop();
            _precipRenderer = fallingObject.GetComponent<ParticleSystemRenderer>();
            _precipRenderer.sharedMaterial = _rainMaterial;
            _precipRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _precipRenderer.receiveShadows = false;

            ConfigureBasePrecipitationSystem(_precip, new Vector3(1.05f, 0.02f, 1.05f), 6000);
        }

        static void ConfigureBasePrecipitationSystem(
            ParticleSystem system, Vector3 emitterScale, int maxParticles)
        {
            var main = system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.loop = true;
            main.playOnAwake = false;
            main.gravityModifier = 0f;
            main.startSpeed = 0f;
            main.maxParticles = maxParticles;

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = emitterScale;

            var emission = system.emission;
            emission.enabled = true;

            var velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;
        }

        void BuildLightning()
        {
            var lightGo = new GameObject("LightningFlash");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0f, _cloudBaseMap, 0f);
            _flashLight = lightGo.AddComponent<Light>();
            _flashLight.type = LightType.Point;
            _flashLight.color = new Color(0.76f, 0.84f, 0.96f);
            _flashLight.range = _config.MapSizeMeters * 2.5f;
            _flashLight.intensity = 0f;
            _flashLight.shadows = LightShadows.None;

            var boltGo = new GameObject("Bolt");
            boltGo.transform.SetParent(transform, false);
            _bolt = boltGo.AddComponent<LineRenderer>();
            _bolt.useWorldSpace = false;
            _bolt.widthMultiplier = 0.004f;
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
            if (amount < 0.08f)
            {
                _clouds.Stop();
                _clouds.Clear();
                return;
            }

            var emission = _clouds.emission;
            emission.rateOverTime = Mathf.Lerp(0f, 46f, amount);

            var main = _clouds.main;
            // Bright cumulus white grading to storm grey.
            Color bright = new Color(0.98f, 0.98f, 1f);
            Color dark = new Color(0.38f, 0.41f, 0.45f);
            Color c = Color.Lerp(bright, dark, Mathf.Clamp01(p.CloudDarkness));
            c.a = Mathf.Lerp(0.16f, 0.48f, amount);
            main.startColor = c;
            main.startSize = new ParticleSystem.MinMaxCurve(0.16f, 0.38f);

            var noise = _clouds.noise;
            noise.strength = Mathf.Lerp(0.018f, 0.052f, amount);
            noise.scrollSpeed = Mathf.Lerp(0.05f, 0.12f, p.WindMs / 15f);

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
            PrecipitationStyle style = ResolvePrecipitationStyle(
                p.Precip, p.PrecipIntensity, _cloudBaseMap, p.WindMs);
            if (!style.Enabled)
            {
                _precip.Stop();
                _precip.Clear();
                return;
            }

            _precipRenderer.sharedMaterial = style.Snow ? _snowMaterial : _rainMaterial;
            _precipRenderer.renderMode = style.Snow
                ? ParticleSystemRenderMode.Billboard
                : ParticleSystemRenderMode.Stretch;
            if (!style.Snow)
            {
                _precipRenderer.lengthScale =
                    Mathf.Lerp(3.0f, 4.0f, Mathf.Clamp01(p.PrecipIntensity));
                _precipRenderer.velocityScale =
                    Mathf.Lerp(0.15f, 0.22f, Mathf.Clamp01(p.PrecipIntensity));
            }

            var main = _precip.main;
            float lifetime = (style.MinLifetime + style.MaxLifetime) * 0.5f;
            float fallSpeed = (style.MinFallSpeed + style.MaxFallSpeed) * 0.5f;
            main.startLifetime = lifetime;
            main.startSize =
                new ParticleSystem.MinMaxCurve(style.MinSize, style.MaxSize);
            main.startColor = style.Snow
                ? new Color(0.94f, 0.97f, 1f, 0.90f)
                : new Color(0.76f, 0.86f, 0.96f, 0.58f);
            main.maxParticles = Mathf.Clamp(
                Mathf.CeilToInt(style.EmissionRate * lifetime) + 256,
                512,
                6000);

            var velocity = _precip.velocityOverLifetime;
            velocity.y = new ParticleSystem.MinMaxCurve(-fallSpeed);
            velocity.x = new ParticleSystem.MinMaxCurve(style.WindDrift);
            velocity.z = new ParticleSystem.MinMaxCurve(style.WindDrift * 0.35f);

            var emission = _precip.emission;
            emission.rateOverTime = style.EmissionRate;

            if (!_precip.isPlaying) _precip.Play();
            _precip.Clear();
        }

        /// <summary>
        /// Resolves particle scale/density from the selected weather profile. The
        /// returned sizes are deliberately several times the previous 0.002-0.012
        /// range: this is a tabletop representation, so physically tiny drops vanish
        /// at headset resolution and must be perceptually scaled.
        /// </summary>
        public static PrecipitationStyle ResolvePrecipitationStyle(
            PrecipKind kind, float intensity, float cloudBaseMap, float windMs)
        {
            float amount = Mathf.Clamp01(intensity);
            if (kind == PrecipKind.None || amount <= 0.001f)
                return default;

            bool snow = kind == PrecipKind.Snow;
            float density = Mathf.Pow(amount, 0.72f);
            float fallSeconds = snow
                ? Mathf.Lerp(4.8f, 3.2f, density)
                : Mathf.Lerp(1.25f, 0.72f, density);
            float averageFallSpeed =
                Mathf.Max(0.01f, cloudBaseMap) / Mathf.Max(0.1f, fallSeconds);

            if (snow)
            {
                return new PrecipitationStyle(
                    true,
                    true,
                    Mathf.Lerp(0.020f, 0.030f, density),
                    Mathf.Lerp(0.044f, 0.065f, density),
                    fallSeconds * 0.84f,
                    fallSeconds * 1.16f,
                    averageFallSpeed * 0.78f,
                    averageFallSpeed * 1.22f,
                    Mathf.Lerp(150f, 520f, density),
                    Mathf.Max(0f, windMs) * 0.006f);
            }

            return new PrecipitationStyle(
                true,
                false,
                Mathf.Lerp(0.0065f, 0.0105f, density),
                Mathf.Lerp(0.013f, 0.021f, density),
                fallSeconds * 0.84f,
                fallSeconds * 1.16f,
                averageFallSpeed * 0.84f,
                averageFallSpeed * 1.18f,
                Mathf.Lerp(240f, 1050f, density),
                Mathf.Max(0f, windMs) * 0.011f);
        }

        void Update()
        {
            if (!_built || !_lightningEnabled) return;

            // Poisson-ish flashes with a quick multi-stroke flicker and decay.
            if (Time.time >= _nextFlashTime)
            {
                _flashEnergy = 1f;
                float u = Mathf.Clamp((float)_rng.NextDouble(), 1e-3f, 1f);
                _nextFlashTime = Time.time + Mathf.Lerp(2.4f, 6.5f, -Mathf.Log(u) * 0.4f);
                StrikeBolt();
            }

            _flashEnergy = Mathf.Max(0f, _flashEnergy - Time.deltaTime * 3.2f);
            // Flicker while decaying so it reads as a real multi-stroke flash.
            float flicker = _flashEnergy * (0.6f + 0.4f * Mathf.Sin(Time.time * 60f));
            _flashLight.intensity = flicker * 3.2f;

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

        static Texture2D SoftCloud(int size, int seed)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "SoftCloud",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var random = new System.Random(seed);
            const int blobCount = 11;
            var centres = new Vector2[blobCount];
            var radii = new float[blobCount];
            for (int i = 0; i < blobCount; i++)
            {
                centres[i] = new Vector2(
                    Mathf.Lerp(-0.52f, 0.52f, (float)random.NextDouble()),
                    Mathf.Lerp(-0.24f, 0.30f, (float)random.NextDouble()));
                radii[i] = Mathf.Lerp(0.22f, 0.48f, (float)random.NextDouble());
            }

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = ((x + 0.5f) / size - 0.5f) * 2f;
                float v = ((y + 0.5f) / size - 0.5f) * 2f;
                float density = 0f;

                for (int i = 0; i < blobCount; i++)
                {
                    float dx = u - centres[i].x;
                    float dy = (v - centres[i].y) * 1.25f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / radii[i];
                    density = Mathf.Max(density, Mathf.SmoothStep(1f, 0f, d));
                }

                float envelope = Mathf.Clamp01(1f - Mathf.Sqrt(u * u * 0.70f + v * v));
                float alpha = density * envelope;
                alpha = alpha * alpha * (3f - 2f * alpha);

                // Baked soft lighting: brighter crown, darker underside and subtle
                // deterministic micro-variation. Sprites remain mobile-cheap while
                // overlapping particles gain a much more volumetric read.
                float vertical = Mathf.InverseLerp(-1f, 1f, v);
                float detail = 0.94f +
                    0.06f * Mathf.Sin((u * 7.3f + v * 5.1f + seed * 0.001f) * 3.7f);
                float shade = Mathf.Lerp(0.64f, 1.0f, vertical) * detail;
                tex.SetPixel(x, y, new Color(
                    shade,
                    Mathf.Lerp(shade * 0.96f, shade, vertical),
                    Mathf.Lerp(shade * 0.91f, shade, vertical),
                    alpha));
            }

            tex.Apply();
            return tex;
        }

        static Texture2D RainStreak(int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "Tapered Rain Streak",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float u = Mathf.Abs((x + 0.5f) / width - 0.5f) * 2f;
                float v = (y + 0.5f) / height;

                // Fine bright core, softer outer water column, and tapered ends.
                // The previous texture had the same opacity from top to bottom, which
                // made stretched drops look like blunt plastic rods.
                float core = Mathf.Pow(Mathf.Clamp01(1f - u), 3.6f);
                float outer = Mathf.Pow(Mathf.Clamp01(1f - u), 1.4f) * 0.34f;
                float headFade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(v / 0.12f));
                float tailFade = Mathf.SmoothStep(
                    0f, 1f, Mathf.Clamp01((1f - v) / 0.22f));
                float longitudinal = headFade * tailFade;
                float alpha = Mathf.Clamp01((core + outer) * longitudinal);
                float glint = Mathf.Lerp(0.78f, 1f, core);

                tex.SetPixel(
                    x,
                    y,
                    new Color(glint * 0.88f, glint * 0.95f, glint, alpha));
            }
            tex.Apply();
            return tex;
        }

        static Texture2D Snowflake(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Procedural Snowflake",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = ((x + 0.5f) / size - 0.5f) * 2f;
                float v = ((y + 0.5f) / size - 0.5f) * 2f;
                float radius = Mathf.Sqrt(u * u + v * v);
                float angle = Mathf.Atan2(v, u);

                // Six crystalline arms, a small centre and two branch rings. The soft
                // halo keeps the shape legible after bilinear filtering in the headset.
                float alignment = Mathf.Abs(Mathf.Cos(angle * 3f));
                float arm = Mathf.Pow(
                    Mathf.Clamp01((alignment - 0.86f) / 0.14f), 1.7f);
                float envelope = Mathf.SmoothStep(
                    1f, 0f, Mathf.InverseLerp(0.12f, 0.94f, radius));
                float centre = Mathf.SmoothStep(
                    1f, 0f, Mathf.InverseLerp(0.04f, 0.20f, radius));
                float branchA = Mathf.Clamp01(
                    1f - Mathf.Abs(radius - 0.43f) / 0.055f) * arm;
                float branchB = Mathf.Clamp01(
                    1f - Mathf.Abs(radius - 0.67f) / 0.050f) * arm;
                float halo = Mathf.Pow(Mathf.Clamp01(1f - radius), 3f) * 0.20f;
                float alpha = Mathf.Clamp01(
                    centre + arm * envelope * 0.94f +
                    branchA * 0.42f + branchB * 0.34f + halo);

                tex.SetPixel(x, y, new Color(0.91f, 0.96f, 1f, alpha));
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
            if (_cloudTexture != null) Destroy(_cloudTexture);
            if (_rainStreak != null) Destroy(_rainStreak);
            if (_snowflake != null) Destroy(_snowflake);
        }
    }
}

using UnityEngine;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Clouds
{
    /// <summary>
    /// Falling-rain particle effect, driven by the same precipitation field the
    /// audio bed and the provenance label already summarise (see
    /// <see cref="Audio.AmbientSoundscape.Apply"/> and
    /// <see cref="Core.ProvenanceLabel.Summarise"/>) but that, until now, had no
    /// visual counterpart at all: precipitation was audible and labelled, never
    /// drawn.
    ///
    /// Droplets are emitted by script rather than the built-in emission/shape
    /// modules, because their spawn position has to track *where the weather grid
    /// says rain is* -- a uniform box would rain uniformly across the whole map
    /// even when the cloud (and so the precipitation) is concentrated along one
    /// side, as a squall line is. Cell selection reuses the weighted inverse-CDF
    /// sampling <see cref="Lightning.LightningDirector.PickStrikeCell"/> already
    /// established for the same problem, weighted linearly by precipitation rate
    /// rather than cubed: rain should cover the whole wet footprint (core plus
    /// trailing stratiform), not just the heaviest cell the way lightning
    /// deliberately concentrates.
    ///
    /// Real fall speed at this scale would be imperceptible -- a 9 m/s raindrop is
    /// 0.0036 VR m/s at 1:2500 -- so the fall is exaggerated for visibility, the
    /// same trade-off <see cref="CloudRenderer"/> already makes for wind-driven
    /// cloud drift (<c>WindTimeScale</c>).
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class RainRenderer : MonoBehaviour
    {
        [Tooltip("Precipitation rate in mm/h at which the effect reaches full intensity.")]
        public float RainFullScaleMmHr = 12f;

        [Tooltip("Particles per second at full intensity. Droplets are small, so this " +
                 "needs to be higher than a coarse-streak effect to still read as rain.")]
        public float MaxEmissionRate = 900f;

        [Tooltip("Seconds a droplet takes to fall from the cloud base to the map plane.")]
        public float FallSeconds = 1.1f;

        [Tooltip("Cells at or below this fraction of RainFullScaleMmHr never spawn rain, " +
                 "so a merely-damp cell at the edge of the storm does not sprinkle.")]
        [Range(0f, 1f)] public float PotentialThreshold = 0.04f;

        ParticleSystem _system;
        ParticleSystemRenderer _renderer;
        Material _material;

        WeatherSnapshot _snapshot;
        float[] _cumulativeWeights;
        float _totalWeight;
        float _intensity;
        float _spawnHeightMapUnits;
        float _emitAccumulator;
        System.Random _random;

        void Awake()
        {
            _system = GetComponent<ParticleSystem>();
            _renderer = GetComponent<ParticleSystemRenderer>();
            ConfigureOnce();
        }

        /// <summary>Retargets emission weighting and fall geometry from a snapshot.</summary>
        public void Apply(WeatherSnapshot snapshot, AppConfig config)
        {
            if (snapshot?.Weather == null || !snapshot.Weather.IsValid)
            {
                Debug.LogWarning("[WeatherVR] RainRenderer got no usable weather; hiding rain.");
                gameObject.SetActive(false);
                _snapshot = null;
                return;
            }

            gameObject.SetActive(true);
            EnsureMaterial();

            _snapshot = snapshot;
            _random = new System.Random(config.ProceduralSeed ^ 0x8A17);
            BuildWeights(snapshot.Weather);

            float meanPrecipitation = MeanPrecipitation(snapshot.Weather);
            _intensity = _totalWeight > 0f ? Mathf.Clamp01(meanPrecipitation / RainFullScaleMmHr) : 0f;

            // Droplets start inside the lower half of the cloud deck -- the same box
            // CloudRenderer sizes from AtmosphereHeightVr -- and fall straight down
            // from there, so they visibly originate from the cloud mass rather than
            // from a fixed altitude independent of it.
            float cloudHeightMapUnits = config.AtmosphereHeightVr / Mathf.Max(config.MapSizeMeters, 1e-4f);
            _spawnHeightMapUnits = cloudHeightMapUnits * 0.55f;
            float fallSpeed = _spawnHeightMapUnits / Mathf.Max(FallSeconds, 0.05f);

            var velocity = _system.velocityOverLifetime;
            velocity.y = new ParticleSystem.MinMaxCurve(-fallSpeed);

            var main = _system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(FallSeconds * 0.85f, FallSeconds * 1.15f);
            main.maxParticles = Mathf.CeilToInt(MaxEmissionRate * FallSeconds * 1.2f) + 64;

            _emitAccumulator = 0f;

            Debug.Log($"[WeatherVR] Rain: mean {meanPrecipitation:F1} mm/h, " +
                      $"intensity {_intensity:F2}, {_intensity * MaxEmissionRate:F0} particles/s.");
        }

        /// <summary>
        /// Weights each cell by its own precipitation rate (zero below threshold),
        /// building the same cumulative-distribution shape
        /// <see cref="Lightning.LightningDirector.BuildWeights"/> uses so
        /// <see cref="PickCell"/> can invert it in O(log n).
        /// </summary>
        void BuildWeights(WeatherDataset weather)
        {
            _cumulativeWeights = new float[weather.cells.Length];
            float threshold = PotentialThreshold * RainFullScaleMmHr;
            float running = 0f;

            for (int i = 0; i < weather.cells.Length; i++)
            {
                float precip = weather.cells[i].precipitationMmHr;
                float weight = precip <= threshold ? 0f : precip;
                running += weight;
                _cumulativeWeights[i] = running;
            }

            _totalWeight = running;
        }

        void Update()
        {
            if (_snapshot == null || _totalWeight <= 0f || _intensity <= 0f) return;

            _emitAccumulator += _intensity * MaxEmissionRate * Time.deltaTime;
            int toEmit = Mathf.FloorToInt(_emitAccumulator);
            if (toEmit <= 0) return;
            _emitAccumulator -= toEmit;

            var weather = _snapshot.Weather;
            var emitParams = new ParticleSystem.EmitParams { applyShapeToPosition = false };

            for (int i = 0; i < toEmit; i++)
            {
                if (!PickCell(out int cellX, out int cellY)) break;

                // Jitter within the cell so repeated draws from the same cell do not
                // rain from exactly the same point, matching
                // LightningDirector.Fire's jitter for the same reason.
                float u = (cellX + (float)_random.NextDouble()) / weather.gridWidth;
                float v = (cellY + (float)_random.NextDouble()) / weather.gridHeight;

                emitParams.position = new Vector3(u - 0.5f, _spawnHeightMapUnits, v - 0.5f);
                _system.Emit(emitParams, 1);
            }
        }

        /// <summary>Inverse-transform sampling of the precipitation-weighted cell distribution.</summary>
        bool PickCell(out int x, out int y)
        {
            x = y = 0;
            if (_cumulativeWeights == null || _totalWeight <= 0f) return false;

            float target = (float)_random.NextDouble() * _totalWeight;

            int low = 0, high = _cumulativeWeights.Length - 1;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (_cumulativeWeights[mid] < target) low = mid + 1;
                else high = mid;
            }

            int width = _snapshot.Weather.gridWidth;
            x = low % width;
            y = low / width;
            return true;
        }

        /// <summary>Fixed setup that never changes between snapshots.</summary>
        void ConfigureOnce()
        {
            var main = _system.main;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.playOnAwake = true;
            main.loop = true;
            // Fall speed comes entirely from velocityOverLifetime below, not gravity.
            main.gravityModifier = 0f;
            main.startSpeed = 0f;
            // Small, city-scaled droplets: the previous 0.6-1.4 cm stretched bars
            // read as blocky slabs against a 2 m map. A believable droplet here is a
            // few millimetres to low-single-digit millimetres of map scale.
            main.startSize = new ParticleSystem.MinMaxCurve(0.0015f, 0.004f);
            main.startColor = new Color(0.78f, 0.87f, 1f, 0.6f);
            main.maxParticles = 4096;

            // Both modules are disabled: position is driven entirely by manual
            // Emit() calls in Update(), weighted by the precipitation field, which
            // neither the emission module's rate nor the shape module's uniform
            // volume can express on their own.
            var emission = _system.emission;
            emission.enabled = false;

            var shape = _system.shape;
            shape.enabled = false;

            var velocity = _system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.Local;

            _renderer.renderMode = ParticleSystemRenderMode.Stretch;
            _renderer.lengthScale = 1.8f;
            _renderer.velocityScale = 0.35f;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
        }

        void EnsureMaterial()
        {
            if (_material != null) return;

            var shader = Shader.Find("WeatherVR/Rain");
            if (shader == null)
            {
                Debug.LogError("[WeatherVR] Rain shader is missing from the build.");
                return;
            }

            _material = new Material(shader) { name = "Rain (runtime)" };
            _renderer.sharedMaterial = _material;
        }

        static float MeanPrecipitation(WeatherDataset weather)
        {
            if (weather?.cells == null || weather.cells.Length == 0) return 0f;

            double sum = 0;
            foreach (var cell in weather.cells) sum += cell.precipitationMmHr;
            return (float)(sum / weather.cells.Length);
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}

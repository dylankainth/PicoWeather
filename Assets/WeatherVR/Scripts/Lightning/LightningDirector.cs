using System.Collections.Generic;
using UnityEngine;
using WeatherVR.Audio;
using WeatherVR.Core;
using WeatherVR.Data;

namespace WeatherVR.Lightning
{
    /// <summary>
    /// Decides where and when lightning strikes, drives the bolt pool, and feeds the
    /// rest of the scene the light those strikes throw off.
    ///
    /// Strike placement is drawn from the weather grid's lightning potential, so the
    /// flashes cluster where the atmosphere is actually unstable and wet rather than
    /// scattering uniformly. Strike *rate* is scaled to the spec's 5-20 per minute
    /// for an active cell.
    /// </summary>
    public class LightningDirector : MonoBehaviour
    {
        [Header("Rate")]
        [Tooltip("Strikes per minute when the most active cell is at full potential.")]
        public float MaxStrikesPerMinute = 20f;

        [Tooltip("Strikes per minute when the storm is marginal but non-zero.")]
        public float MinStrikesPerMinute = 4f;

        [Tooltip("Cells below this potential are never struck.")]
        [Range(0f, 1f)] public float PotentialThreshold = 0.06f;

        [Header("Geometry")]
        [Tooltip("Altitude in real metres where the channel leaves the cloud base.")]
        public float ChannelTopAltitudeMeters = 2400f;

        [Header("Ambient response")]
        [Tooltip("How strongly a strike lifts the scene's ambient light.")]
        [Range(0f, 4f)] public float AmbientFlashStrength = 1.4f;

        [Tooltip("Colour a strike pushes the ambient light towards.")]
        public Color FlashAmbient = new Color(0.62f, 0.72f, 1f);

        [Header("Audio")]
        [Tooltip("Real thunder from the far side of a 50 km map would arrive over two " +
                 "minutes later, which no demo can use. Delays are compressed by this " +
                 "factor; relative ordering and near/far contrast are preserved.")]
        public float ThunderDelayCompression = 0.03f;

        static readonly int BoltCountId = Shader.PropertyToID("_BoltCount");
        static readonly int BoltPositionsId = Shader.PropertyToID("_BoltPositions");
        static readonly int BoltColorsId = Shader.PropertyToID("_BoltColors");

        /// <summary>Must match MAX_BOLTS in VolumetricClouds.shader.</summary>
        const int ShaderBoltCapacity = 4;

        readonly List<LightningBolt> _bolts = new List<LightningBolt>();
        readonly Vector4[] _boltPositions = new Vector4[ShaderBoltCapacity];
        readonly Vector4[] _boltColors = new Vector4[ShaderBoltCapacity];

        WeatherSnapshot _snapshot;
        AppConfig _config;
        ThunderAudio _thunder;
        Material _boltMaterial;

        // Cumulative distribution over cells, weighted by lightning potential.
        float[] _cumulativeWeights;
        float _totalWeight;
        float _meanPotential;
        float _peakPotential;

        float _nextStrikeTime;
        int _strikeCounter;
        System.Random _random;

        Color _baseAmbient;
        bool _baseAmbientCaptured;

        /// <summary>Total strikes fired since the snapshot was applied. Shown in the HUD.</summary>
        public int StrikeCount => _strikeCounter;

        /// <summary>Strikes per minute the current dataset implies.</summary>
        public float CurrentRatePerMinute { get; private set; }

        void Awake()
        {
            _thunder = GetComponent<ThunderAudio>();
            if (_thunder == null) _thunder = gameObject.AddComponent<ThunderAudio>();
        }

        /// <summary>Configures the director for a snapshot and starts the storm.</summary>
        public void Apply(WeatherSnapshot snapshot, AppConfig config, Transform mapRoot)
        {
            _snapshot = snapshot;
            _config = config;
            _random = new System.Random(config.ProceduralSeed ^ 0x5EED);

            if (!_baseAmbientCaptured)
            {
                _baseAmbient = RenderSettings.ambientLight;
                _baseAmbientCaptured = true;
            }

            BuildWeights(snapshot.Weather);
            EnsurePool(config.MaxConcurrentBolts, mapRoot);

            CurrentRatePerMinute = _totalWeight <= 0f
                ? 0f
                : Mathf.Lerp(MinStrikesPerMinute, MaxStrikesPerMinute, _peakPotential);

            _strikeCounter = 0;
            ScheduleNextStrike();

            Debug.Log($"[WeatherVR] Lightning: peak potential {_peakPotential:F2}, " +
                      $"mean {_meanPotential:F2}, {CurrentRatePerMinute:F1} strikes/min.");
        }

        void BuildWeights(WeatherDataset weather)
        {
            _totalWeight = 0f;
            _meanPotential = 0f;
            _peakPotential = 0f;

            if (weather?.cells == null || weather.cells.Length == 0)
            {
                _cumulativeWeights = null;
                return;
            }

            _cumulativeWeights = new float[weather.cells.Length];
            float running = 0f;

            for (int i = 0; i < weather.cells.Length; i++)
            {
                float potential = Mathf.Clamp01(weather.cells[i].lightningPotential);
                _meanPotential += potential;
                _peakPotential = Mathf.Max(_peakPotential, potential);

                // Cube the weight so strikes concentrate in the genuinely active core
                // rather than sprinkling across everywhere that is merely damp.
                float weight = potential < PotentialThreshold ? 0f : potential * potential * potential;
                running += weight;
                _cumulativeWeights[i] = running;
            }

            _meanPotential /= weather.cells.Length;
            _totalWeight = running;
        }

        void EnsurePool(int size, Transform mapRoot)
        {
            EnsureBoltMaterial();

            while (_bolts.Count < size)
            {
                var boltObject = new GameObject($"LightningBolt{_bolts.Count}");
                boltObject.transform.SetParent(mapRoot, worldPositionStays: false);

                var filter = boltObject.AddComponent<MeshFilter>();
                var meshRenderer = boltObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = _boltMaterial;

                var bolt = boltObject.AddComponent<LightningBolt>();
                _bolts.Add(bolt);
            }

            for (int i = 0; i < _bolts.Count; i++)
                _bolts[i].gameObject.SetActive(i < size);
        }

        void EnsureBoltMaterial()
        {
            if (_boltMaterial != null) return;

            var shader = Shader.Find("WeatherVR/LightningBolt");
            if (shader == null)
            {
                Debug.LogError("[WeatherVR] LightningBolt shader is missing from the build.");
                return;
            }
            _boltMaterial = new Material(shader) { name = "LightningBolt (runtime)" };
        }

        void Update()
        {
            if (_snapshot == null || _cumulativeWeights == null) return;

            if (_totalWeight > 0f && Time.time >= _nextStrikeTime)
            {
                Fire();
                ScheduleNextStrike();
            }

            UpdateShaderGlobals();
            UpdateAmbient();
        }

        void ScheduleNextStrike()
        {
            if (_totalWeight <= 0f || CurrentRatePerMinute <= 0f)
            {
                _nextStrikeTime = float.MaxValue;
                return;
            }

            // Strikes are a Poisson process: exponentially distributed gaps, not a
            // metronome. This is the difference between a storm and a strobe light.
            float meanInterval = 60f / CurrentRatePerMinute;
            float u = Mathf.Clamp((float)_random.NextDouble(), 1e-4f, 1f);
            _nextStrikeTime = Time.time + -Mathf.Log(u) * meanInterval;
        }

        void Fire()
        {
            var bolt = FindIdleBolt();
            if (bolt == null) return;

            if (!PickStrikeCell(out int cellX, out int cellY)) return;

            var weather = _snapshot.Weather;
            // Jitter within the cell so repeated strikes in an active cell do not
            // stack on exactly the same point.
            float u = (cellX + (float)_random.NextDouble()) / weather.gridWidth;
            float v = (cellY + (float)_random.NextDouble()) / weather.gridHeight;
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);

            float terrainElevation = _snapshot.Terrain != null ? _snapshot.Terrain.SampleElevation(u, v) : 0f;

            // Bolts live under the map root, whose local space is the normalised
            // [-0.5, 0.5] map square. Both ends go through the same conversions the
            // terrain mesh and the cloud box use, so the channel genuinely runs from
            // the cloud base down to the ground surface.
            float groundY = _config.TerrainElevationToMapUnits(terrainElevation);
            float topY = _config.AltitudeToMapUnits(ChannelTopAltitudeMeters);

            var ground = new Vector3(u - 0.5f, groundY, v - 0.5f);
            var top = new Vector3(
                ground.x + ((float)_random.NextDouble() - 0.5f) * 0.06f,
                topY,
                ground.z + ((float)_random.NextDouble() - 0.5f) * 0.06f);

            bolt.Strike(top, ground, _random.Next());
            _strikeCounter++;

            // Thunder: real travel time, then compressed so the demo stays watchable.
            float distanceMeters = Vector3.Distance(Camera.main != null
                    ? Camera.main.transform.position
                    : transform.position,
                bolt.GroundPointWorld) / Mathf.Max(_config.HorizontalScale, 1e-9f);
            float realDelay = distanceMeters / 343f;
            float delay = realDelay * ThunderDelayCompression;

            _thunder?.PlayThunder(bolt.GroundPointWorld, distanceMeters / 1000f, delay);
        }

        LightningBolt FindIdleBolt()
        {
            foreach (var bolt in _bolts)
                if (bolt.gameObject.activeSelf && !bolt.IsActive) return bolt;
            return null;
        }

        /// <summary>Inverse-transform sampling of the potential-weighted cell distribution.</summary>
        bool PickStrikeCell(out int x, out int y)
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

        /// <summary>
        /// Publishes active bolts to the cloud shader so the flash lights the cloud
        /// from inside, which is most of what sells a storm at a distance.
        /// </summary>
        void UpdateShaderGlobals()
        {
            int count = 0;
            for (int i = 0; i < _bolts.Count && count < ShaderBoltCapacity; i++)
            {
                var bolt = _bolts[i];
                if (!bolt.IsActive || bolt.CurrentIntensity <= 0.001f) continue;

                Vector3 position = bolt.GroundPointWorld;
                // Radius in world units over which the in-scatter falls off, scaled to
                // the map so it stays right if the user resizes it.
                float radius = _config != null ? _config.MapSizeMeters * 0.35f : 0.7f;

                _boltPositions[count] = new Vector4(position.x, position.y, position.z, radius);

                Color color = bolt.BoltColor * bolt.CurrentIntensity * 1.8f;
                _boltColors[count] = new Vector4(color.r, color.g, color.b, 1f);
                count++;
            }

            // Zero the unused slots: stale values would keep lighting the volume.
            for (int i = count; i < ShaderBoltCapacity; i++)
            {
                _boltPositions[i] = Vector4.zero;
                _boltColors[i] = Vector4.zero;
            }

            Shader.SetGlobalInt(BoltCountId, count);
            Shader.SetGlobalVectorArray(BoltPositionsId, _boltPositions);
            Shader.SetGlobalVectorArray(BoltColorsId, _boltColors);
        }

        /// <summary>Lifts the scene ambient while a bolt is bright, then lets it fall back.</summary>
        void UpdateAmbient()
        {
            float brightest = 0f;
            foreach (var bolt in _bolts)
                if (bolt.IsActive) brightest = Mathf.Max(brightest, bolt.CurrentIntensity);

            RenderSettings.ambientLight = Color.Lerp(
                _baseAmbient,
                _baseAmbient + FlashAmbient * AmbientFlashStrength,
                Mathf.Clamp01(brightest));
        }

        void OnDisable()
        {
            // Leave no residue if the director is switched off mid-flash.
            Shader.SetGlobalInt(BoltCountId, 0);
            if (_baseAmbientCaptured) RenderSettings.ambientLight = _baseAmbient;
        }

        void OnDestroy()
        {
            if (_boltMaterial != null) Destroy(_boltMaterial);
        }
    }
}

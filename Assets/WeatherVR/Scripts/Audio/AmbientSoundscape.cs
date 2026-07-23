using UnityEngine;
using WeatherVR.Data;

namespace WeatherVR.Audio
{
    /// <summary>
    /// The wind and rain bed under the storm.
    ///
    /// Both beds are always playing; what changes is their level, driven by the
    /// snapshot's mean wind speed and precipitation rate. Crossfading levels rather
    /// than starting and stopping clips avoids the audible seam you get when a loop
    /// begins mid-demo, and means the soundscape genuinely tracks the data: a dry
    /// snapshot is quiet, a squall line is not.
    ///
    /// The beds are non-spatialised. They are the ambience of the weather system the
    /// user is looking into, not a sound emanating from a 2 m object on a table.
    /// </summary>
    public class AmbientSoundscape : MonoBehaviour
    {
        [Range(0f, 1f)] public float MasterVolume = 0.45f;

        [Tooltip("Wind speed in m/s at which the wind bed reaches full level.")]
        public float WindFullScaleMs = 14f;

        [Tooltip("Precipitation rate in mm/h at which the rain bed reaches full level.")]
        public float RainFullScaleMmHr = 8f;

        [Tooltip("Seconds for a level change to take effect. Weather does not switch instantly.")]
        public float FadeSeconds = 2.5f;

        [Tooltip("Length of each generated loop. Longer costs memory but repeats less obviously.")]
        [Range(2f, 12f)] public float LoopSeconds = 8f;

        public int Seed = 20260723;

        AudioSource _wind;
        AudioSource _rain;
        float _windTarget;
        float _rainTarget;

        void Awake()
        {
            _wind = CreateBed("WindBed", ProceduralAudio.CreateWindLoop(LoopSeconds, Seed));
            _rain = CreateBed("RainBed", ProceduralAudio.CreateRainLoop(LoopSeconds, Seed + 17));
        }

        AudioSource CreateBed(string name, AudioClip clip)
        {
            var bedObject = new GameObject(name);
            bedObject.transform.SetParent(transform, worldPositionStays: false);

            var source = bedObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            source.Play();
            return source;
        }

        /// <summary>Retargets the beds from a snapshot's mean conditions.</summary>
        public void Apply(WeatherSnapshot snapshot)
        {
            var weather = snapshot?.Weather;
            if (weather?.cells == null || weather.cells.Length == 0)
            {
                _windTarget = 0f;
                _rainTarget = 0f;
                return;
            }

            double windSum = 0, precipitationSum = 0;
            foreach (var cell in weather.cells)
            {
                windSum += Mathf.Sqrt(cell.windU * cell.windU + cell.windV * cell.windV);
                precipitationSum += cell.precipitationMmHr;
            }

            float meanWind = (float)(windSum / weather.cells.Length);
            float meanPrecipitation = (float)(precipitationSum / weather.cells.Length);

            // A floor on the wind bed: total silence around a storm reads as a bug.
            _windTarget = Mathf.Lerp(0.22f, 1f, Mathf.Clamp01(meanWind / WindFullScaleMs));
            _rainTarget = Mathf.Clamp01(meanPrecipitation / RainFullScaleMmHr);

            Debug.Log($"[WeatherVR] Soundscape: mean wind {meanWind:F1} m/s, " +
                      $"mean precip {meanPrecipitation:F2} mm/h.");
        }

        void Update()
        {
            if (_wind == null || _rain == null) return;

            float rate = FadeSeconds > 0f ? Time.deltaTime / FadeSeconds : 1f;
            _wind.volume = Mathf.MoveTowards(_wind.volume, _windTarget * MasterVolume, rate);
            _rain.volume = Mathf.MoveTowards(_rain.volume, _rainTarget * MasterVolume, rate);
        }

        void OnDestroy()
        {
            if (_wind != null && _wind.clip != null) Destroy(_wind.clip);
            if (_rain != null && _rain.clip != null) Destroy(_rain.clip);
        }
    }
}

using TMPro;
using UnityEngine;
using WeatherVR.Data;
using WeatherVR.Lightning;

namespace WeatherVR.Core
{
    /// <summary>
    /// The small panel floating at the edge of the map saying what the user is
    /// actually looking at.
    ///
    /// This is not a debug overlay, and it is not optional. Roughly half of what is
    /// on screen is derived rather than measured: the lightning is a CAPE-and-rain
    /// proxy because no free feed publishes stroke density, the cloud shapes are
    /// noise because no 31 km model resolves a cumulus tower, and when the venue
    /// Wi-Fi is down the whole scene is procedural. Someone watching a demo cannot
    /// tell any of that by looking, so the app says so.
    /// </summary>
    public class ProvenanceLabel : MonoBehaviour
    {
        [Tooltip("Text element to write into. Found in children if unset.")]
        public TMP_Text Text;

        [Tooltip("Optional second line showing frame time and quality tier.")]
        public bool ShowPerformance = true;

        [Tooltip("Seconds between refreshes of the live portion.")]
        public float RefreshInterval = 0.5f;

        /// <summary>Set by <see cref="WeatherSceneController"/>.</summary>
        [System.NonSerialized] public PerfGovernor Governor;

        /// <summary>Set by <see cref="WeatherSceneController"/>.</summary>
        [System.NonSerialized] public LightningDirector Lightning;

        WeatherSnapshot _snapshot;
        AppConfig _config;
        string _staticBlock = "";
        string _status;
        float _nextRefresh;

        void Awake()
        {
            if (Text == null) Text = GetComponentInChildren<TMP_Text>();
        }

        /// <summary>Shows a one-line status while the scene is still loading.</summary>
        public void SetStatus(string status)
        {
            _status = status;
            Render();
        }

        /// <summary>Switches the label from status text to the full provenance block.</summary>
        public void SetSnapshot(WeatherSnapshot snapshot, AppConfig config)
        {
            _snapshot = snapshot;
            _config = config;
            _status = null;

            var weather = snapshot.Weather;
            var builder = new System.Text.StringBuilder(512);

            builder.AppendLine("<b>Shanghai</b>  31.23°N 121.47°E");
            builder.AppendLine($"{config.RegionSpanKm:F0} km across · shown at {config.MapSizeMeters:F1} m " +
                               $"(1:{1f / config.HorizontalScale:N0})");
            builder.AppendLine($"Altitude ×{config.VerticalExaggeration:F1} vs. horizontal");
            builder.AppendLine();

            if (weather != null)
            {
                builder.AppendLine($"<b>Observed</b> {FormatTime(weather.observationTimeUtc)}");
                builder.AppendLine($"Terrain: {snapshot.TerrainSource}");
                builder.AppendLine($"Imagery: {snapshot.SatelliteSource}");
                builder.AppendLine($"Weather: {snapshot.WeatherSource}");
                builder.AppendLine();

                Summarise(weather, out float meanCloud, out float meanPrecipitation, out float peakCape);
                builder.AppendLine($"Cloud cover {meanCloud * 100f:F0}%  ·  " +
                                   $"rain {meanPrecipitation:F1} mm/h  ·  CAPE {peakCape:F0} J/kg");
            }

            builder.AppendLine();
            builder.AppendLine("<i>Cloud shapes are procedural detail over a coarse model grid.</i>");
            builder.AppendLine("<i>Lightning is derived from CAPE and rain rate, not observed strokes.</i>");

            _staticBlock = builder.ToString();
            Render();
        }

        void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Render();
        }

        void Render()
        {
            if (Text == null) return;

            if (_status != null)
            {
                Text.text = _status;
                return;
            }

            if (_snapshot == null) return;

            var builder = new System.Text.StringBuilder(_staticBlock, 640);

            if (Lightning != null)
            {
                builder.AppendLine();
                builder.AppendLine($"Strikes: {Lightning.StrikeCount} " +
                                   $"({Lightning.CurrentRatePerMinute:F0}/min)");
            }

            if (ShowPerformance && Governor != null && Governor.AverageFrameMs > 0f)
            {
                float fps = 1000f / Governor.AverageFrameMs;
                builder.AppendLine($"{fps:F0} fps · clouds: {Governor.CurrentTierName}");
            }

            Text.text = builder.ToString();
        }

        static void Summarise(WeatherDataset weather, out float meanCloud,
                              out float meanPrecipitation, out float peakCape)
        {
            meanCloud = 0f;
            meanPrecipitation = 0f;
            peakCape = 0f;

            if (weather?.cells == null || weather.cells.Length == 0) return;

            double cloudSum = 0, precipitationSum = 0;
            foreach (var cell in weather.cells)
            {
                cloudSum += cell.cloudTotal;
                precipitationSum += cell.precipitationMmHr;
                if (cell.capeJkg > peakCape) peakCape = cell.capeJkg;
            }

            meanCloud = (float)(cloudSum / weather.cells.Length);
            meanPrecipitation = (float)(precipitationSum / weather.cells.Length);
        }

        static string FormatTime(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "unknown time";
            return System.DateTime.TryParse(
                iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed.ToString("yyyy-MM-dd HH:mm 'UTC'")
                : iso;
        }
    }
}

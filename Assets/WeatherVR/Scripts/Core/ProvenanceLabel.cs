using UnityEngine;
using UnityEngine.UI;
using WeatherVR.Data;
using WeatherVR.Lightning;

namespace WeatherVR.Core
{
    /// <summary>
    /// A set of floating 3D info-cards at the edge of the map showing what the
    /// user is actually looking at. Each card is a separate world-space canvas
    /// at a slightly different depth, giving a holographic-HUD feel.
    ///
    /// This is not a debug overlay, and it is not optional. Roughly half of what
    /// is on screen is derived rather than measured: the lightning is a CAPE-and-rain
    /// proxy because no free feed publishes stroke density, the cloud shapes are
    /// noise because no 31 km model resolves a cumulus tower, and when the venue
    /// Wi-Fi is down the whole scene is procedural. Someone watching a demo cannot
    /// tell any of that by looking, so the app says so.
    /// </summary>
    public class ProvenanceLabel : MonoBehaviour
    {
        [System.Serializable]
        public struct CardInfo
        {
            public Text Text;
            public Image Background;
        }

        [Tooltip("Card slots. Assigned by the scene builder; falls back to " +
                 "GetComponentInChildren if unset.")]
        public CardInfo[] Cards;

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
        string _status;
        float _nextRefresh;

        void Awake()
        {
            if (Cards == null || Cards.Length == 0)
            {
                var texts = GetComponentsInChildren<Text>();
                if (texts.Length > 0)
                {
                    Cards = new CardInfo[texts.Length];
                    for (int i = 0; i < texts.Length; i++)
                        Cards[i] = new CardInfo { Text = texts[i] };
                }
            }
        }

        /// <summary>Shows a one-line status while the scene is still loading.</summary>
        public void SetStatus(string status)
        {
            _status = status;
            _snapshot = null;
            Render();
        }

        /// <summary>Switches the cards from status text to the full provenance block.</summary>
        public void SetSnapshot(WeatherSnapshot snapshot, AppConfig config)
        {
            _snapshot = snapshot;
            _config = config;
            _status = null;
            RenderCards();
        }

        void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Render();
        }

        void Render()
        {
            if (_status != null)
            {
                ShowStatusOnAllCards();
                return;
            }
            if (_snapshot != null)
            {
                RenderCards();
            }
        }

        void ShowStatusOnAllCards()
        {
            if (Cards == null) return;
            for (int i = 0; i < Cards.Length; i++)
            {
                if (Cards[i].Text == null) continue;
                if (i == 0)
                {
                    // Built-in UI Text does not parse <align> (that is TextMeshPro
                    // markup); it rendered as literal text during loading. Centre via
                    // the Text component's own alignment field instead.
                    Cards[i].Text.alignment = TextAnchor.MiddleCenter;
                    Cards[i].Text.text = $"<color=#5CB8FF>{_status}</color>";
                }
                else
                {
                    Cards[i].Text.text = "";
                }
                if (Cards[i].Background != null)
                    Cards[i].Background.enabled = (i == 0);
            }
        }

        void RenderCards()
        {
            if (Cards == null || Cards.Length == 0) return;

            var weather = _snapshot.Weather;

            // ── Card 0 : Location ──────────────────────────────────────
            if (Cards.Length > 0 && Cards[0].Text != null)
            {
                // Undo the centred alignment ShowStatusOnAllCards sets while loading.
                Cards[0].Text.alignment = TextAnchor.UpperLeft;

                var b = new System.Text.StringBuilder(128);
                b.AppendLine($"<color=#5CB8FF><b>London</b></color>");
                b.AppendLine($"{FormatCoordinate(_config.CenterLatitude, "N", "S")}");
                b.AppendLine($"{FormatCoordinate(_config.CenterLongitude, "E", "W")}");
                b.AppendLine();
                b.AppendLine($"<color=#8899AA>{_config.RegionSpanKm:F0} km across</color>");
                b.AppendLine($"<color=#8899AA>{_config.MapSizeMeters:F1} m · " +
                             $"1:{1f / _config.HorizontalScale:N0}</color>");
                Cards[0].Text.text = b.ToString();
            }

            // ── Card 1 : Weather ───────────────────────────────────────
            if (Cards.Length > 1 && Cards[1].Text != null)
            {
                var b = new System.Text.StringBuilder(256);
                b.AppendLine($"<color=#5CB8FF><b>Weather</b></color>");

                if (weather != null)
                {
                    b.AppendLine($"<color=#5CB8FF>Observed</color>  " +
                                 $"<color=#FFCC80>{FormatTime(weather.observationTimeUtc)}</color>");
                    b.AppendLine($"<color=#8899AA>Terrain:</color> {_snapshot.TerrainSource}");
                    b.AppendLine($"<color=#8899AA>Imagery:</color> {_snapshot.SatelliteSource}");
                    b.AppendLine($"<color=#8899AA>Weather:</color> {_snapshot.WeatherSource}");
                    b.AppendLine();

                    Summarise(weather, out float meanCloud, out float meanPrecipitation, out float peakCape);
                    b.AppendLine($"<color=#FFCC80>Cloud {meanCloud * 100f:F0}%</color>  ·  " +
                                 $"<color=#80D8FF>Rain {meanPrecipitation:F1} mm/h</color>");
                    b.AppendLine($"<color=#FFAB91>CAPE {peakCape:F0} J/kg</color>");

                    if (Lightning != null)
                    {
                        b.AppendLine();
                        b.AppendLine($"<color=#FFE082>Strikes: {Lightning.StrikeCount}</color>  " +
                                     $"<color=#8899AA>({Lightning.CurrentRatePerMinute:F0}/min)</color>");
                    }
                }
                else
                {
                    b.AppendLine("<color=#8899AA>No data</color>");
                }

                if (ShowPerformance && Governor != null && Governor.AverageFrameMs > 0f)
                {
                    float fps = 1000f / Governor.AverageFrameMs;
                    b.AppendLine($"<color=#8899AA>{fps:F0} fps · {Governor.CurrentTierName}</color>");
                }

                Cards[1].Text.text = b.ToString();
            }

            // ── Card 2 : Data Sources ──────────────────────────────────
            if (Cards.Length > 2 && Cards[2].Text != null)
            {
                var b = new System.Text.StringBuilder(128);
                b.AppendLine($"<color=#5CB8FF><b>Sources</b></color>");
                if (weather != null)
                {
                    b.AppendLine($"<color=#8899AA>Terrain:</color> {_snapshot.TerrainSource}");
                    b.AppendLine($"<color=#8899AA>Imagery:</color> {_snapshot.SatelliteSource}");
                    b.AppendLine($"<color=#8899AA>Weather:</color> {_snapshot.WeatherSource}");
                }
                else
                {
                    b.AppendLine("<color=#8899AA>Procedural</color>");
                }
                b.AppendLine();
                b.AppendLine("<color=#667788><i>Clouds: procedural detail</i></color>");
                b.AppendLine("<color=#667788><i>Lightning: CAPE-derived</i></color>");
                Cards[2].Text.text = b.ToString();
            }
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

        static string FormatCoordinate(double value, string posLabel, string negLabel)
        {
            var label = value >= 0 ? posLabel : negLabel;
            var abs = System.Math.Abs(value);
            var deg = (int)abs;
            var minFrac = (abs - deg) * 60.0;
            var min = (int)minFrac;
            var sec = (minFrac - min) * 60.0;
            return $"{deg}°{min:D2}'{sec:F1}\"{label}";
        }
    }
}

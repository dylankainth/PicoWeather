using System;
using UnityEngine;

namespace WeatherVR.Data
{
    /// <summary>
    /// One real day of <c>tools/fetch_forecast.py</c>'s 5-day bake.
    ///
    /// The per-hour condition is stored as parallel start-hour/kind arrays rather
    /// than a jagged array of segments — <see cref="JsonUtility"/> cannot deserialise
    /// an array-of-arrays, the same reason <see cref="BuildingRecord"/> flattens its
    /// footprint. <see cref="KindAtHour"/> resolves an hour against them; converting
    /// to whatever segment type the UI layer wants is that layer's job, not this
    /// data class's — this file has no reason to know what a carousel card is.
    /// </summary>
    [Serializable]
    public class ForecastDay
    {
        public string dateIso;
        public string dayEnglish;
        public string dayChinese;

        /// <summary>The day's headline condition, as <c>(int)WeatherSceneKind</c> —
        /// Open-Meteo's own daily weathercode, mapped. Not forced to Thunderstorm
        /// even on the peak-storm day: a real quiet week should look quiet.</summary>
        public int headlineSceneKind;

        public float temperatureMaxC;
        public float temperatureMinC;
        public float humidityPercent;
        public float windKmh;
        public float rainChancePercent;

        /// <summary>Hours (of 24) whose weathercode was a literal thunderstorm code
        /// (WMO 95/96/99). Usually 0 for London — see <see cref="ForecastDataset.PeakStormDayIndex"/>.</summary>
        public int thunderstormHours;

        public float peakCapeJkg;
        public float totalPrecipMm;
        public float peakGustKmh;

        /// <summary>Relative storm-likelihood score, comparable only against the other
        /// days in the same bake — see <c>fetch_forecast.py::storm_score</c>.</summary>
        public float stormScore;

        /// <summary>Hour each segment starts, ascending, first entry always 0.</summary>
        public float[] segmentStartHours = Array.Empty<float>();

        /// <summary>Scene kind for the segment starting at the same index in
        /// <see cref="segmentStartHours"/>, as <c>(int)WeatherSceneKind</c>.</summary>
        public int[] segmentSceneKinds = Array.Empty<int>();

        /// <summary>Which case is showing at a given hour of this day. Mirrors
        /// <c>WeatherCarouselItem.KindAtHour</c>'s resolution rule exactly (latest
        /// segment starting at or before <paramref name="hour"/>, wrapping to the
        /// last segment for an hour before all of them) so a real day and an authored
        /// one behave identically to the carousel's time slider.</summary>
        public int KindAtHour(float hour)
        {
            if (segmentStartHours == null || segmentStartHours.Length == 0)
                return headlineSceneKind;

            hour = Mathf.Repeat(hour, 24f);

            int chosen = -1;
            float bestStart = float.NegativeInfinity;
            int latest = 0;
            float latestStart = float.NegativeInfinity;

            for (int i = 0; i < segmentStartHours.Length; i++)
            {
                float start = segmentStartHours[i];
                if (start > latestStart) { latestStart = start; latest = i; }
                if (start <= hour && start > bestStart) { bestStart = start; chosen = i; }
            }

            return segmentSceneKinds[chosen >= 0 ? chosen : latest];
        }

        /// <summary>True if any segment of this day resolves to <paramref name="kind"/>.</summary>
        public bool HasKind(int kind)
        {
            if (segmentSceneKinds == null || segmentSceneKinds.Length == 0)
                return headlineSceneKind == kind;

            for (int i = 0; i < segmentSceneKinds.Length; i++)
                if (segmentSceneKinds[i] == kind) return true;
            return false;
        }
    }

    /// <summary>
    /// The real 5-day hourly forecast baked by <c>tools/fetch_forecast.py</c>, plus
    /// which day is relatively most storm-prone. Serialised with
    /// <see cref="JsonUtility"/> — see <see cref="ForecastDay"/> for why the timeline
    /// is flattened.
    /// </summary>
    [Serializable]
    public class ForecastDataset
    {
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;
        public string generatedUtc;

        /// <summary>"open-meteo" or "procedural".</summary>
        public string source = "procedural";
        public string attribution = "";
        public string timezone = "";

        public double centerLatitude;
        public double centerLongitude;

        /// <summary>Index into <see cref="days"/> of the real day this bake judged
        /// relatively most storm-prone. This — not a literal Thunderstorm case — is
        /// what the flood simulator's storm-surge controls key off, because a real
        /// forecast usually never reaches a literal storm code at all.</summary>
        public int peakStormDayIndex;

        public ForecastDay[] days = Array.Empty<ForecastDay>();

        public bool IsValid =>
            days != null && days.Length > 0 &&
            peakStormDayIndex >= 0 && peakStormDayIndex < days.Length;

        public ForecastDay PeakStormDay => IsValid ? days[peakStormDayIndex] : null;

        public string Describe() =>
            $"{source} · {days.Length} day(s) · peak storm day {peakStormDayIndex}";
    }
}

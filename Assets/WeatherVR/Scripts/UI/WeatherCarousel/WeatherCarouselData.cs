using System;
using System.Collections;
using UnityEngine;
using WeatherVR.Data;

namespace WeatherVR.UI.Carousel
{
    public enum WeatherCarouselIcon
    {
        Sunny,
        PartlyCloudy,
        Cloudy,
        Rain,
        Storm
    }

    [Serializable]
    public sealed class WeatherCarouselItem
    {
        public string DayEnglish;
        public string DayChinese;
        public string DateEnglish;
        public string ConditionEnglish;
        public string ConditionChinese;
        public int TemperatureC;
        public int RainChance;
        public int Humidity;
        public int WindKmh;
        public WeatherCarouselIcon Icon;
        public Color Accent;
        public Color GlassTint;
    }

    public sealed class WeatherCarouselDataset
    {
        public string LocationEnglish = "LONDON";
        public string LocationChinese = "伦敦";
        public string SourceEnglish = "SCENE DATA";
        public string SourceChinese = "场景数据";
        public WeatherCarouselItem[] Items;
    }

    /// <summary>
    /// Small provider boundary between the carousel and its data source.
    /// A backend implementation only has to produce a WeatherCarouselDataset.
    /// It does not need to know anything about Unity UI.
    /// </summary>
    public interface IWeatherCarouselDataProvider
    {
        IEnumerator Load(
            WeatherSnapshot sceneSnapshot,
            Action<WeatherCarouselDataset> completed,
            Action<string> failed);
    }

    public static class WeatherCarouselDataProvider
    {
        public static IWeatherCarouselDataProvider CreateDefault()
        {
            // ================================================================
            // LIVE DATA API INTEGRATION POINT
            // HERE IS WHERE YOU CONNECT YOUR BACKEND OR LIVE WEATHER API.
            // RETURN YOUR OWN IWeatherCarouselDataProvider IMPLEMENTATION HERE.
            //
            // EXAMPLE:
            // return new MyLiveWeatherApiProvider("https://api.example.com");
            // ================================================================
            return new SceneWeatherCarouselDataProvider();
        }
    }

    /// <summary>
    /// Default zero-setup provider. It reads the weather snapshot the app already
    /// loaded, then creates a short presentation deck from it. Future cards are a
    /// deterministic demo projection until a real forecast backend is connected.
    /// </summary>
    public sealed class SceneWeatherCarouselDataProvider : IWeatherCarouselDataProvider
    {
        static readonly int[] TemperatureOffsets = { 0, -1, -3, -2, 1 };
        static readonly float[] CloudOffsets = { 0f, 0.12f, 0.34f, 0.22f, -0.10f };
        static readonly float[] RainOffsets = { 0f, 0.05f, 0.40f, 0.24f, -0.04f };

        public IEnumerator Load(
            WeatherSnapshot sceneSnapshot,
            Action<WeatherCarouselDataset> completed,
            Action<string> failed)
        {
            // Keep the provider coroutine-shaped so replacing it with
            // UnityWebRequest later does not require changes to the UI feature.
            yield return null;

            if (sceneSnapshot?.Weather?.cells == null ||
                sceneSnapshot.Weather.cells.Length == 0)
            {
                failed?.Invoke("The scene did not provide weather cells.");
                yield break;
            }

            Summarise(
                sceneSnapshot.Weather,
                out float temperature,
                out float cloud,
                out float precipitation,
                out float wind);

            var dataset = new WeatherCarouselDataset();
            SetSourceLabel(dataset, sceneSnapshot.WeatherSource);
            dataset.Items = new WeatherCarouselItem[TemperatureOffsets.Length];

            DateTime start = ObservationDate(sceneSnapshot.Weather.observationTimeUtc);
            for (int i = 0; i < dataset.Items.Length; i++)
            {
                float cardCloud = Mathf.Clamp01(cloud + CloudOffsets[i]);
                float cardRain = Mathf.Max(0f, precipitation + RainOffsets[i]);
                DateTime date = start.AddDays(i);

                ResolveCondition(
                    cardCloud,
                    cardRain,
                    out string english,
                    out string chinese,
                    out WeatherCarouselIcon icon,
                    out Color accent,
                    out Color tint);

                dataset.Items[i] = new WeatherCarouselItem
                {
                    DayEnglish = i == 0 ? "NOW" : date.ToString("ddd").ToUpperInvariant(),
                    DayChinese = i == 0 ? "现在" : ChineseDay(date.DayOfWeek),
                    DateEnglish = date.ToString("dd MMM").ToUpperInvariant(),
                    ConditionEnglish = english,
                    ConditionChinese = chinese,
                    TemperatureC = Mathf.RoundToInt(temperature) + TemperatureOffsets[i],
                    RainChance = Mathf.Clamp(Mathf.RoundToInt(cardRain * 34f + cardCloud * 18f), 2, 96),
                    Humidity = Mathf.Clamp(Mathf.RoundToInt(38f + cardCloud * 46f + cardRain * 3f), 35, 96),
                    WindKmh = Mathf.Max(1, Mathf.RoundToInt(wind * 3.6f + i * 1.5f)),
                    Icon = icon,
                    Accent = accent,
                    GlassTint = tint
                };
            }

            completed?.Invoke(dataset);
        }

        static void Summarise(
            WeatherDataset weather,
            out float temperature,
            out float cloud,
            out float precipitation,
            out float wind)
        {
            double temperatureTotal = 0;
            double cloudTotal = 0;
            double precipitationTotal = 0;
            double windTotal = 0;

            foreach (WeatherCell cell in weather.cells)
            {
                temperatureTotal += cell.temperatureC;
                cloudTotal += cell.cloudTotal;
                precipitationTotal += cell.precipitationMmHr;
                windTotal += Math.Sqrt(cell.windU * cell.windU + cell.windV * cell.windV);
            }

            float count = weather.cells.Length;
            temperature = (float)(temperatureTotal / count);
            cloud = (float)(cloudTotal / count);
            precipitation = (float)(precipitationTotal / count);
            wind = (float)(windTotal / count);
        }

        static void SetSourceLabel(WeatherCarouselDataset dataset, string source)
        {
            string value = (source ?? string.Empty).ToLowerInvariant();
            if (value.Contains("live") || value.Contains("open-meteo"))
            {
                dataset.SourceEnglish = "LIVE DATA";
                dataset.SourceChinese = "实时数据";
            }
            else if (value.Contains("procedural") || value.Contains("demo"))
            {
                dataset.SourceEnglish = "DEMO DATA";
                dataset.SourceChinese = "演示数据";
            }
            else
            {
                dataset.SourceEnglish = "OFFLINE DATA";
                dataset.SourceChinese = "离线数据";
            }
        }

        static DateTime ObservationDate(string iso)
        {
            return DateTime.TryParse(
                iso,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTime parsed)
                ? parsed
                : DateTime.UtcNow;
        }

        static string ChineseDay(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return "周一";
                case DayOfWeek.Tuesday: return "周二";
                case DayOfWeek.Wednesday: return "周三";
                case DayOfWeek.Thursday: return "周四";
                case DayOfWeek.Friday: return "周五";
                case DayOfWeek.Saturday: return "周六";
                default: return "周日";
            }
        }

        static void ResolveCondition(
            float cloud,
            float rain,
            out string english,
            out string chinese,
            out WeatherCarouselIcon icon,
            out Color accent,
            out Color tint)
        {
            if (rain >= 1.8f)
            {
                english = "Thunderstorms";
                chinese = "雷暴";
                icon = WeatherCarouselIcon.Storm;
                accent = new Color(0.73f, 0.66f, 1f);
                tint = new Color(0.26f, 0.27f, 0.43f, 0.72f);
            }
            else if (rain >= 0.18f)
            {
                english = "Rain";
                chinese = "降雨";
                icon = WeatherCarouselIcon.Rain;
                accent = new Color(0.43f, 0.80f, 1f);
                tint = new Color(0.16f, 0.32f, 0.44f, 0.72f);
            }
            else if (cloud >= 0.72f)
            {
                english = "Cloudy";
                chinese = "多云";
                icon = WeatherCarouselIcon.Cloudy;
                accent = new Color(0.78f, 0.88f, 0.96f);
                tint = new Color(0.23f, 0.31f, 0.40f, 0.72f);
            }
            else if (cloud >= 0.30f)
            {
                english = "Partly cloudy";
                chinese = "局部多云";
                icon = WeatherCarouselIcon.PartlyCloudy;
                accent = new Color(1f, 0.81f, 0.39f);
                tint = new Color(0.23f, 0.34f, 0.53f, 0.72f);
            }
            else
            {
                english = "Clear skies";
                chinese = "晴朗";
                icon = WeatherCarouselIcon.Sunny;
                accent = new Color(1f, 0.82f, 0.38f);
                tint = new Color(0.30f, 0.25f, 0.66f, 0.76f);
            }
        }
    }
}

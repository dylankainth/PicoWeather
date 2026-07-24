using System;
using System.Collections;
using UnityEngine;
using WeatherVR.Data;
using WeatherVR.Weather;

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

        /// <summary>Which weather scene this card selects, as <c>(int)WeatherSceneKind</c>.</summary>
        public int SceneKind;
    }

    public sealed class WeatherCarouselDataset
    {
        public string LocationEnglish = "LONDON";
        public string LocationChinese = "伦敦";
        public string SourceEnglish = "WEATHER PICKER";
        public string SourceChinese = "天气选择";
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
            // The carousel is now a weather PICKER: one card per weather case.
            // Tapping a card switches the rendered scene. To drive it from a live
            // forecast instead, return your own IWeatherCarouselDataProvider here.
            // ================================================================
            return new SceneKindCarouselDataProvider();
        }
    }

    /// <summary>
    /// Emits one card per <see cref="WeatherSceneKind"/>, so every weather case is
    /// directly selectable. No live data needed — the card carries the scene it picks.
    /// </summary>
    public sealed class SceneKindCarouselDataProvider : IWeatherCarouselDataProvider
    {
        public IEnumerator Load(
            WeatherSnapshot sceneSnapshot,
            Action<WeatherCarouselDataset> completed,
            Action<string> failed)
        {
            // Kept coroutine-shaped so a live backend can drop in without changing the UI.
            yield return null;

            var kinds = WeatherScene.AllKinds;
            var dataset = new WeatherCarouselDataset
            {
                Items = new WeatherCarouselItem[kinds.Length]
            };

            for (int i = 0; i < kinds.Length; i++)
            {
                WeatherSceneKind kind = kinds[i];
                WeatherSceneProfile p = WeatherScene.Default(kind);

                Describe(kind, out string chinese, out WeatherCarouselIcon icon,
                         out Color accent, out Color tint, out int temperature,
                         out int rainChance, out int humidity);

                dataset.Items[i] = new WeatherCarouselItem
                {
                    DayEnglish = p.DisplayName.ToUpperInvariant(),
                    DayChinese = chinese,
                    DateEnglish = "",
                    ConditionEnglish = p.DisplayName,
                    ConditionChinese = chinese,
                    TemperatureC = temperature,
                    RainChance = rainChance,
                    Humidity = humidity,
                    WindKmh = Mathf.RoundToInt(p.WindMs * 3.6f),
                    Icon = icon,
                    Accent = accent,
                    GlassTint = tint,
                    SceneKind = (int)kind
                };
            }

            completed?.Invoke(dataset);
        }

        static void Describe(
            WeatherSceneKind kind,
            out string chinese,
            out WeatherCarouselIcon icon,
            out Color accent,
            out Color tint,
            out int temperature,
            out int rainChance,
            out int humidity)
        {
            switch (kind)
            {
                case WeatherSceneKind.Clear:
                    chinese = "晴朗"; icon = WeatherCarouselIcon.Sunny;
                    accent = new Color(1f, 0.82f, 0.38f); tint = new Color(0.30f, 0.25f, 0.66f, 0.76f);
                    temperature = 24; rainChance = 2; humidity = 42; return;
                case WeatherSceneKind.PartlyCloudy:
                    chinese = "局部多云"; icon = WeatherCarouselIcon.PartlyCloudy;
                    accent = new Color(1f, 0.81f, 0.39f); tint = new Color(0.23f, 0.34f, 0.53f, 0.72f);
                    temperature = 21; rainChance = 10; humidity = 55; return;
                case WeatherSceneKind.Cloudy:
                    chinese = "多云"; icon = WeatherCarouselIcon.Cloudy;
                    accent = new Color(0.78f, 0.88f, 0.96f); tint = new Color(0.23f, 0.31f, 0.40f, 0.72f);
                    temperature = 17; rainChance = 25; humidity = 68; return;
                case WeatherSceneKind.Overcast:
                    chinese = "阴天"; icon = WeatherCarouselIcon.Cloudy;
                    accent = new Color(0.70f, 0.76f, 0.82f); tint = new Color(0.22f, 0.26f, 0.32f, 0.74f);
                    temperature = 15; rainChance = 35; humidity = 74; return;
                case WeatherSceneKind.Fog:
                    chinese = "雾"; icon = WeatherCarouselIcon.Cloudy;
                    accent = new Color(0.82f, 0.86f, 0.90f); tint = new Color(0.40f, 0.43f, 0.47f, 0.74f);
                    temperature = 12; rainChance = 20; humidity = 96; return;
                case WeatherSceneKind.Drizzle:
                    chinese = "毛毛雨"; icon = WeatherCarouselIcon.Rain;
                    accent = new Color(0.55f, 0.82f, 1f); tint = new Color(0.18f, 0.30f, 0.42f, 0.72f);
                    temperature = 13; rainChance = 60; humidity = 88; return;
                case WeatherSceneKind.Rain:
                    chinese = "降雨"; icon = WeatherCarouselIcon.Rain;
                    accent = new Color(0.43f, 0.80f, 1f); tint = new Color(0.16f, 0.32f, 0.44f, 0.72f);
                    temperature = 12; rainChance = 85; humidity = 92; return;
                case WeatherSceneKind.Thunderstorm:
                    chinese = "雷暴"; icon = WeatherCarouselIcon.Storm;
                    accent = new Color(0.73f, 0.66f, 1f); tint = new Color(0.26f, 0.27f, 0.43f, 0.72f);
                    temperature = 18; rainChance = 95; humidity = 90; return;
                default: // Snow
                    chinese = "雪"; icon = WeatherCarouselIcon.Cloudy;
                    accent = new Color(0.86f, 0.92f, 1f); tint = new Color(0.34f, 0.40f, 0.52f, 0.74f);
                    temperature = -1; rainChance = 70; humidity = 84; return;
            }
        }
    }
}

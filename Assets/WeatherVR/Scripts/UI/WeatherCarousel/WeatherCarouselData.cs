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

    /// <summary>
    /// One stretch of a day that renders as a single weather case: "from
    /// <see cref="StartHour"/> until the next segment's start, it is this kind".
    ///
    /// Stored as a start-time list rather than 24 per-hour entries because that is
    /// how the data reads when authored ("the front arrives at 14:00") and because
    /// the thing downstream cares about is precisely the boundary: crossing one is
    /// the only moment the expensive scene rebuild has to run.
    /// </summary>
    [Serializable]
    public struct WeatherTimeSegment
    {
        [Tooltip("Hour of day this segment starts, inclusive. 0..24.")]
        public float StartHour;

        /// <summary>The scene to render during this segment, as <c>(int)WeatherSceneKind</c>.</summary>
        public int SceneKind;
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

        /// <summary>
        /// The card's headline scene, as <c>(int)WeatherSceneKind</c> — what the day is
        /// remembered as, and the fallback for a card with no <see cref="Timeline"/>.
        /// </summary>
        public int SceneKind;

        /// <summary>
        /// This day's weather hour by hour, driven by the carousel's time slider.
        /// May be null or empty, in which case the whole day is <see cref="SceneKind"/> —
        /// that is what keeps datasets built before the slider existed (see
        /// <c>VisualReviewCapture</c>) working unchanged.
        /// </summary>
        public WeatherTimeSegment[] Timeline;

        /// <summary>
        /// True for the one day a real forecast (<see cref="ForecastCarouselDataProvider"/>)
        /// judged relatively most storm-prone. A real forecast for London usually never
        /// reaches a literal Thunderstorm case at all — see <c>fetch_forecast.py</c>'s
        /// storm-likelihood score — so <see cref="HasKind"/> alone would leave the
        /// storm-surge controls unreachable on every real day. Always false for the
        /// authored demo week, where a literal Thunderstorm segment does the same job.
        /// </summary>
        public bool IsPeakStormDay;

        /// <summary>
        /// Which case is showing at a given hour of day.
        ///
        /// Picks the latest segment starting at or before <paramref name="hour"/>, and
        /// deliberately does not assume the segments are sorted — an unsorted timeline
        /// would otherwise resolve to something arbitrary rather than obviously wrong.
        /// An hour before every segment's start wraps to the last segment of the day,
        /// which is the overnight one.
        /// </summary>
        public WeatherSceneKind KindAtHour(float hour)
        {
            if (Timeline == null || Timeline.Length == 0)
                return (WeatherSceneKind)SceneKind;

            hour = Mathf.Repeat(hour, 24f);

            int chosen = -1;
            float bestStart = float.NegativeInfinity;
            int latest = 0;
            float latestStart = float.NegativeInfinity;

            for (int i = 0; i < Timeline.Length; i++)
            {
                float start = Timeline[i].StartHour;
                if (start > latestStart) { latestStart = start; latest = i; }
                if (start <= hour && start > bestStart) { bestStart = start; chosen = i; }
            }

            return (WeatherSceneKind)Timeline[chosen >= 0 ? chosen : latest].SceneKind;
        }

        /// <summary>
        /// Whether this day's timeline ever shows <paramref name="kind"/> at any hour —
        /// used to decide whether a control tied to that case (the storm-surge presets,
        /// for Thunderstorm) should be reachable at all while this day is selected,
        /// independent of whatever hour the clock happens to be parked at. Falls back to
        /// the headline <see cref="SceneKind"/> for the no-timeline case, same as
        /// <see cref="KindAtHour"/>.
        /// </summary>
        public bool HasKind(WeatherSceneKind kind)
        {
            if (Timeline == null || Timeline.Length == 0)
                return (WeatherSceneKind)SceneKind == kind;

            for (int i = 0; i < Timeline.Length; i++)
            {
                if ((WeatherSceneKind)Timeline[i].SceneKind == kind)
                    return true;
            }
            return false;
        }
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
            // The carousel shows one card per DAY, and the time slider picks the
            // hour within the selected day. Weather comes from that day's timeline,
            // so the same day reads differently at 03:00 and at 15:00.
            //
            // Authored week is the default, not the real forecast: the demo wants
            // every WeatherSceneKind reachable, and a real 5-day London forecast
            // frequently has no storm and no snow at all (the first live bake of
            // fetch_forecast.py had zero thunderstorm hours in the whole window) —
            // reachability, not realism, is the point of this picker for a demo.
            //
            // ForecastCarouselDataProvider (real baked forecast, tools/fetch_forecast.py)
            // still exists and still works — swap it back in via
            // FallbackCarouselDataProvider(new ForecastCarouselDataProvider(), new
            // DayTimelineCarouselDataProvider()) for a "what's actually forecast"
            // mode instead of a "show off every case" one.
            //
            // SceneKindCarouselDataProvider below is the older flat picker (one card
            // per case, no timeline). Kept because it is still the quickest way to
            // reach a specific case when debugging one.
            // ================================================================
            return new DayTimelineCarouselDataProvider();
        }
    }

    /// <summary>
    /// Tries <paramref name="primary"/>; if it produces no usable dataset, tries
    /// <paramref name="fallback"/>. Mirrors <c>WeatherDataService</c>'s own
    /// "best source available, procedural fallback always succeeds" shape, applied
    /// to the carousel's data layer instead of the scene's.
    /// </summary>
    public sealed class FallbackCarouselDataProvider : IWeatherCarouselDataProvider
    {
        readonly IWeatherCarouselDataProvider primary;
        readonly IWeatherCarouselDataProvider fallback;

        public FallbackCarouselDataProvider(
            IWeatherCarouselDataProvider primary, IWeatherCarouselDataProvider fallback)
        {
            this.primary = primary;
            this.fallback = fallback;
        }

        public IEnumerator Load(
            WeatherSnapshot sceneSnapshot,
            Action<WeatherCarouselDataset> completed,
            Action<string> failed)
        {
            WeatherCarouselDataset dataset = null;
            string error = null;

            yield return primary.Load(sceneSnapshot, value => dataset = value, message => error = message);

            if (dataset?.Items != null && dataset.Items.Length > 0)
            {
                completed?.Invoke(dataset);
                yield break;
            }

            if (!string.IsNullOrEmpty(error))
                Debug.Log($"[WeatherVR] {error}; falling back to the authored week.");

            yield return fallback.Load(sceneSnapshot, completed, failed);
        }
    }

    /// <summary>
    /// Five day cards, each carrying an hour-by-hour timeline for the slider to scrub.
    ///
    /// The week is hand-authored under two constraints. Every one of the nine
    /// <see cref="WeatherSceneKind"/> cases has to appear somewhere, because the
    /// carousel used to be a flat one-card-per-case picker and moving to day cards
    /// must not leave any scene unreachable. And each day has to read as a plausible
    /// day rather than a shuffle: fog burning off into sun, a front arriving as
    /// drizzle then rain, a storm peaking in the afternoon heat and easing by night.
    /// </summary>
    public sealed class DayTimelineCarouselDataProvider : IWeatherCarouselDataProvider
    {
        static WeatherTimeSegment Seg(float hour, WeatherSceneKind kind) =>
            new WeatherTimeSegment { StartHour = hour, SceneKind = (int)kind };

        static readonly WeatherTimeSegment[][] Week =
        {
            // Today — river fog before dawn, then a clear bright day.
            new[]
            {
                Seg(0f,  WeatherSceneKind.Fog),
                Seg(7f,  WeatherSceneKind.PartlyCloudy),
                Seg(11f, WeatherSceneKind.Clear),
                Seg(17f, WeatherSceneKind.PartlyCloudy),
                Seg(21f, WeatherSceneKind.Clear),
            },
            // Tomorrow — a warm front arrives through the afternoon.
            new[]
            {
                Seg(0f,  WeatherSceneKind.Cloudy),
                Seg(9f,  WeatherSceneKind.Overcast),
                Seg(14f, WeatherSceneKind.Drizzle),
                Seg(18f, WeatherSceneKind.Rain),
                Seg(22f, WeatherSceneKind.Cloudy),
            },
            // Day 3 — the unstable one: storms build in the afternoon.
            new[]
            {
                Seg(0f,  WeatherSceneKind.Overcast),
                Seg(6f,  WeatherSceneKind.Rain),
                Seg(12f, WeatherSceneKind.Thunderstorm),
                Seg(17f, WeatherSceneKind.Rain),
                Seg(21f, WeatherSceneKind.Overcast),
            },
            // Day 4 — clearing behind the front, cloud building by midday.
            new[]
            {
                Seg(0f,  WeatherSceneKind.Clear),
                Seg(5f,  WeatherSceneKind.PartlyCloudy),
                Seg(10f, WeatherSceneKind.Cloudy),
                Seg(15f, WeatherSceneKind.PartlyCloudy),
                Seg(20f, WeatherSceneKind.Clear),
            },
            // Day 5 — cold air in: snow, a lull, more snow, freezing fog overnight.
            new[]
            {
                Seg(0f,  WeatherSceneKind.Snow),
                Seg(8f,  WeatherSceneKind.Overcast),
                Seg(13f, WeatherSceneKind.Snow),
                Seg(19f, WeatherSceneKind.Fog),
            },
        };

        /// <summary>What each day is remembered as — the card's icon and headline
        /// condition. Authored rather than derived: the most memorable weather of a
        /// day is rarely whatever happens to be showing at noon.</summary>
        static readonly WeatherSceneKind[] Headlines =
        {
            WeatherSceneKind.Clear,
            WeatherSceneKind.Rain,
            WeatherSceneKind.Thunderstorm,
            WeatherSceneKind.PartlyCloudy,
            WeatherSceneKind.Snow,
        };

        public IEnumerator Load(
            WeatherSnapshot sceneSnapshot,
            Action<WeatherCarouselDataset> completed,
            Action<string> failed)
        {
            // Kept coroutine-shaped so a live backend can drop in without changing the UI.
            yield return null;

            var dataset = new WeatherCarouselDataset
            {
                SourceEnglish = "5 DAY x 24 HOUR",
                SourceChinese = "五天 · 24小时",
                Items = new WeatherCarouselItem[Week.Length]
            };

            DateTime today = DateTime.Now.Date;

            for (int day = 0; day < Week.Length; day++)
            {
                WeatherSceneKind headline = Headlines[day];
                WeatherSceneProfile p = WeatherScene.Default(headline);

                SceneKindCarouselDataProvider.Describe(
                    headline, out string chinese, out WeatherCarouselIcon icon,
                    out int temperature, out int rainChance, out int humidity);

                DateTime date = today.AddDays(day);
                DayLabel(day, date, out string dayEnglish, out string dayChinese);

                dataset.Items[day] = new WeatherCarouselItem
                {
                    DayEnglish = dayEnglish,
                    DayChinese = dayChinese,
                    DateEnglish = date.ToString("d MMM").ToUpperInvariant(),
                    ConditionEnglish = p.DisplayName,
                    ConditionChinese = chinese,
                    TemperatureC = temperature,
                    RainChance = rainChance,
                    Humidity = humidity,
                    WindKmh = Mathf.RoundToInt(p.WindMs * 3.6f),
                    Icon = icon,
                    // Accent/tint come from the profile itself (WeatherScene.cs) rather
                    // than a second hardcoded palette here, so the card, the pedestal,
                    // the floor and the sky can never drift apart on a given case.
                    Accent = p.GlassAccent,
                    GlassTint = p.GlassTint,
                    SceneKind = (int)headline,
                    Timeline = Week[day]
                };
            }

            completed?.Invoke(dataset);
        }

        /// <summary>Internal rather than private because <see cref="ForecastCarouselDataProvider"/>
        /// needs the same TODAY/TOMORROW/weekday convention for real dates — two
        /// copies of this would be two things to keep in step, same reasoning as
        /// <see cref="SceneKindCarouselDataProvider.Describe"/> below.</summary>
        internal static void DayLabel(int offset, DateTime date, out string english, out string chinese)
        {
            switch (offset)
            {
                case 0: english = "TODAY"; chinese = "今天"; return;
                case 1: english = "TOMORROW"; chinese = "明天"; return;
                default:
                    english = date.ToString("dddd").ToUpperInvariant();
                    chinese = ChineseWeekday(date.DayOfWeek);
                    return;
            }
        }

        internal static string ChineseWeekday(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Monday: return "星期一";
                case DayOfWeek.Tuesday: return "星期二";
                case DayOfWeek.Wednesday: return "星期三";
                case DayOfWeek.Thursday: return "星期四";
                case DayOfWeek.Friday: return "星期五";
                case DayOfWeek.Saturday: return "星期六";
                default: return "星期日";
            }
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

                // Accent/tint come from the profile itself (WeatherScene.cs) rather
                // than a second hardcoded palette here, so the card, the pedestal,
                // the floor and the sky can never drift apart on a given case.
                Describe(kind, out string chinese, out WeatherCarouselIcon icon,
                         out int temperature, out int rainChance, out int humidity);

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
                    Accent = p.GlassAccent,
                    GlassTint = p.GlassTint,
                    SceneKind = (int)kind
                };
            }

            completed?.Invoke(dataset);
        }

        /// <summary>
        /// Per-case presentation values. Internal rather than private because
        /// <see cref="DayTimelineCarouselDataProvider"/> needs the same table for its
        /// headline card — two copies of this would be two things to keep in step.
        /// </summary>
        internal static void Describe(
            WeatherSceneKind kind,
            out string chinese,
            out WeatherCarouselIcon icon,
            out int temperature,
            out int rainChance,
            out int humidity)
        {
            switch (kind)
            {
                case WeatherSceneKind.Clear:
                    chinese = "晴朗"; icon = WeatherCarouselIcon.Sunny;
                    temperature = 24; rainChance = 2; humidity = 42; return;
                case WeatherSceneKind.PartlyCloudy:
                    chinese = "局部多云"; icon = WeatherCarouselIcon.PartlyCloudy;
                    temperature = 21; rainChance = 10; humidity = 55; return;
                case WeatherSceneKind.Cloudy:
                    chinese = "多云"; icon = WeatherCarouselIcon.Cloudy;
                    temperature = 17; rainChance = 25; humidity = 68; return;
                case WeatherSceneKind.Overcast:
                    chinese = "阴天"; icon = WeatherCarouselIcon.Cloudy;
                    temperature = 15; rainChance = 35; humidity = 74; return;
                case WeatherSceneKind.Fog:
                    chinese = "雾"; icon = WeatherCarouselIcon.Cloudy;
                    temperature = 12; rainChance = 20; humidity = 96; return;
                case WeatherSceneKind.Drizzle:
                    chinese = "毛毛雨"; icon = WeatherCarouselIcon.Rain;
                    temperature = 13; rainChance = 60; humidity = 88; return;
                case WeatherSceneKind.Rain:
                    chinese = "降雨"; icon = WeatherCarouselIcon.Rain;
                    temperature = 12; rainChance = 85; humidity = 92; return;
                case WeatherSceneKind.Thunderstorm:
                    chinese = "雷暴"; icon = WeatherCarouselIcon.Storm;
                    temperature = 18; rainChance = 95; humidity = 90; return;
                default: // Snow
                    chinese = "雪"; icon = WeatherCarouselIcon.Cloudy;
                    temperature = -1; rainChance = 70; humidity = 84; return;
            }
        }
    }

    /// <summary>
    /// Five day cards built from the real 5-day hourly forecast baked by
    /// <c>tools/fetch_forecast.py</c> — same shape as <see cref="DayTimelineCarouselDataProvider"/>
    /// (one card per day, a real timeline for the slider to scrub), but every number
    /// on it is what Open-Meteo actually said instead of an authored week.
    ///
    /// Fails (via <paramref name="failed"/>, no <paramref name="completed"/> call) when
    /// <see cref="WeatherSnapshot.Forecast"/> is missing or invalid, so
    /// <see cref="FallbackCarouselDataProvider"/> drops back to the authored week —
    /// this class does not need its own fallback logic.
    /// </summary>
    public sealed class ForecastCarouselDataProvider : IWeatherCarouselDataProvider
    {
        public IEnumerator Load(
            WeatherSnapshot sceneSnapshot,
            Action<WeatherCarouselDataset> completed,
            Action<string> failed)
        {
            yield return null;

            ForecastDataset forecast = sceneSnapshot?.Forecast;
            if (forecast == null || !forecast.IsValid)
            {
                failed?.Invoke("No baked forecast available");
                yield break;
            }

            var dataset = new WeatherCarouselDataset
            {
                SourceEnglish = "5 DAY x 24 HOUR",
                SourceChinese = "五天 · 24小时",
                Items = new WeatherCarouselItem[forecast.days.Length]
            };

            for (int day = 0; day < forecast.days.Length; day++)
            {
                ForecastDay real = forecast.days[day];
                var headline = (WeatherSceneKind)real.headlineSceneKind;
                WeatherSceneProfile profile = WeatherScene.Default(headline);

                // Chinese label/icon/accent/tint come from the same tables every other
                // provider uses, so a real day and an authored one look identical for
                // the same case — only the numbers and the timeline are real here.
                SceneKindCarouselDataProvider.Describe(
                    headline, out string chinese, out WeatherCarouselIcon icon,
                    out _, out _, out _);

                DateTime date = DateTime.TryParse(
                    real.dateIso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out DateTime parsed)
                    ? parsed
                    : DateTime.Now.Date.AddDays(day);

                DayTimelineCarouselDataProvider.DayLabel(
                    day, date, out string dayEnglish, out string dayChinese);

                dataset.Items[day] = new WeatherCarouselItem
                {
                    DayEnglish = dayEnglish,
                    DayChinese = dayChinese,
                    DateEnglish = date.ToString("d MMM").ToUpperInvariant(),
                    ConditionEnglish = profile.DisplayName,
                    ConditionChinese = chinese,
                    TemperatureC = Mathf.RoundToInt(real.temperatureMaxC),
                    RainChance = Mathf.RoundToInt(real.rainChancePercent),
                    Humidity = Mathf.RoundToInt(real.humidityPercent),
                    WindKmh = Mathf.RoundToInt(real.windKmh),
                    Icon = icon,
                    Accent = profile.GlassAccent,
                    GlassTint = profile.GlassTint,
                    SceneKind = real.headlineSceneKind,
                    Timeline = ToSegments(real),
                    IsPeakStormDay = day == forecast.peakStormDayIndex,
                };
            }

            completed?.Invoke(dataset);
        }

        static WeatherTimeSegment[] ToSegments(ForecastDay day)
        {
            int count = day.segmentStartHours?.Length ?? 0;
            var segments = new WeatherTimeSegment[count];
            for (int i = 0; i < count; i++)
            {
                segments[i] = new WeatherTimeSegment
                {
                    StartHour = day.segmentStartHours[i],
                    SceneKind = day.segmentSceneKinds[i],
                };
            }
            return segments;
        }
    }
}

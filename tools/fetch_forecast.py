"""Bake a real 5-day hourly forecast to Assets/StreamingAssets/WeatherData/forecast.json.

Source: Open-Meteo, the same free key-less endpoint ``fetch_weather.py`` uses for
the live snapshot -- but where that file captures *one instant* (cloud cover,
CAPE, precipitation right now), this one captures the *next five days*, hour by
hour, so the carousel's day cards and time slider can show what the forecast
actually says instead of an authored week.

Two things this produces that the authored week (``DayTimelineCarouselDataProvider``)
could not:

* A real per-hour condition timeline, mapped from Open-Meteo's WMO weathercode
  to the app's ``WeatherSceneKind`` -- see ``WEATHERCODE_TO_KIND``.
* A **storm-likelihood score** for each of the five days, so the app can point
  at "the day most likely to see a storm" even when the real forecast (as it
  usually is for London) never actually reaches a literal thunderstorm code.
  That day is what the flood simulator's storm-surge controls key off, per
  ``peakStormDayIndex`` -- a designated day, not a literal weather case, which
  is exactly what a UK summer forecast usually offers instead of a storm.

This does not attempt to forecast a flood. It answers a narrower, honest
question: of the real days ahead, which one is relatively most storm-prone,
so that a demo of "what if that turned into a surge" has a real day to attach
to rather than an invented one.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import sys

import geo

SCHEMA_VERSION = 1

OPEN_METEO_ENDPOINT = "https://api.open-meteo.com/v1/forecast"
HOURLY_VARS = (
    "weathercode,precipitation,precipitation_probability,cape,"
    "windspeed_10m,windgusts_10m,relative_humidity_2m,temperature_2m"
)
DAILY_VARS = (
    "weathercode,temperature_2m_max,temperature_2m_min,precipitation_sum,"
    "precipitation_probability_max,windspeed_10m_max"
)
ATTRIBUTION = "Forecast by Open-Meteo.com (CC BY 4.0), ECMWF/DWD/NOAA source models."
TIMEZONE = "Europe/London"

FORECAST_DAYS = 5
HOURS_PER_DAY = 24

# WMO weathercode -> WeatherSceneKind (int), matching the enum order in
# WeatherScene.cs: Clear, PartlyCloudy, Cloudy, Overcast, Fog, Drizzle, Rain,
# Thunderstorm, Snow. Unlisted codes fall back to Cloudy (2) -- the safest
# "something, not nothing" default for a code this table does not recognise.
WEATHERCODE_TO_KIND = {
    0: 0,                                  # clear sky
    1: 1,                                  # mainly clear
    2: 2,                                  # partly cloudy
    3: 3,                                  # overcast
    45: 4, 48: 4,                          # fog, rime fog
    51: 5, 53: 5, 55: 5, 56: 5, 57: 5,     # drizzle (incl. freezing)
    61: 6, 63: 6, 65: 6, 66: 6, 67: 6,     # rain (incl. freezing)
    80: 6, 81: 6, 82: 6,                   # rain showers
    71: 8, 73: 8, 75: 8, 77: 8, 85: 8, 86: 8,  # snow, snow showers
    95: 7, 96: 7, 99: 7,                   # thunderstorm (+ hail)
}
KIND_CLOUDY_FALLBACK = 2
THUNDERSTORM_CODES = (95, 96, 99)


def kind_for_weathercode(code) -> int:
    try:
        return WEATHERCODE_TO_KIND.get(int(code), KIND_CLOUDY_FALLBACK)
    except (TypeError, ValueError):
        return KIND_CLOUDY_FALLBACK


def fetch(center_lat: float, center_lon: float, timeout: float) -> dict:
    import requests

    params = {
        "latitude": f"{center_lat:.4f}",
        "longitude": f"{center_lon:.4f}",
        "hourly": HOURLY_VARS,
        "daily": DAILY_VARS,
        "forecast_days": FORECAST_DAYS,
        "timezone": TIMEZONE,
    }
    response = requests.get(OPEN_METEO_ENDPOINT, params=params, timeout=timeout)
    response.raise_for_status()
    return response.json()


def storm_score(thunder_hours: int, peak_cape: float, total_precip_mm: float,
                peak_gust_kmh: float) -> float:
    """Higher = more storm-like. Not calibrated against any hazard scale --
    only used to rank the five real days against each other, so only the
    ordering matters, not the absolute value.

    A literal thunderstorm code dominates the score outright (the model is
    telling us directly), CAPE and gust speed reward atmospheric instability
    even without one, and precipitation is weighted lightly on its own --
    steady drizzle should not outrank a sharp, unstable, gusty day just
    because it drops more total rain.
    """
    return (
        thunder_hours * 1000.0
        + max(peak_cape, 0.0)
        + max(total_precip_mm, 0.0) * 10.0
        + max(peak_gust_kmh, 0.0) * 2.0
    )


def collapse_to_segments(hourly_kinds: list[int]) -> list[tuple[float, int]]:
    """Hour-by-hour kinds to (start_hour, kind) segments, matching the
    WeatherTimeSegment shape the carousel already reads via KindAtHour."""
    segments = [(0.0, hourly_kinds[0])]
    for hour in range(1, len(hourly_kinds)):
        if hourly_kinds[hour] != segments[-1][1]:
            segments.append((float(hour), hourly_kinds[hour]))
    return segments


def day_label(offset: int, date: dt.date) -> tuple[str, str]:
    """Mirrors WeatherCarouselData.cs's DayLabel exactly, so a real day and an
    authored one render identically for offsets 0/1, and by real weekday
    thereafter."""
    if offset == 0:
        return "TODAY", "今天"
    if offset == 1:
        return "TOMORROW", "明天"

    weekday_zh = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"]
    return date.strftime("%A").upper(), weekday_zh[date.weekday()]


def build_days(payload: dict) -> tuple[list[dict], int]:
    hourly = payload.get("hourly", {}) or {}
    daily = payload.get("daily", {}) or {}

    hourly_time = hourly.get("time") or []
    expected_hours = FORECAST_DAYS * HOURS_PER_DAY
    if len(hourly_time) < expected_hours:
        raise RuntimeError(
            f"expected {expected_hours} hourly entries, got {len(hourly_time)}"
        )

    daily_time = daily.get("time") or []
    if len(daily_time) < FORECAST_DAYS:
        raise RuntimeError(
            f"expected {FORECAST_DAYS} daily entries, got {len(daily_time)}"
        )

    def hourly_series(name, default=0.0):
        raw = hourly.get(name) or []
        return [float(v) if v is not None else default for v in raw]

    weathercodes = hourly_series("weathercode")
    precip = hourly_series("precipitation")
    rain_chance_hourly = hourly_series("precipitation_probability")
    cape = hourly_series("cape")
    wind = hourly_series("windspeed_10m")
    gust = hourly_series("windgusts_10m")
    humidity = hourly_series("relative_humidity_2m")

    daily_weathercode = daily.get("weathercode") or []
    daily_temp_max = daily.get("temperature_2m_max") or []
    daily_temp_min = daily.get("temperature_2m_min") or []
    daily_rain_chance = daily.get("precipitation_probability_max") or []
    daily_wind_max = daily.get("windspeed_10m_max") or []

    days = []
    scores = []

    for day in range(FORECAST_DAYS):
        start = day * HOURS_PER_DAY
        end = start + HOURS_PER_DAY

        hour_kinds = [kind_for_weathercode(c) for c in weathercodes[start:end]]
        segments = collapse_to_segments(hour_kinds)

        day_codes = [int(c) for c in weathercodes[start:end]]
        thunder_hours = sum(1 for c in day_codes if c in THUNDERSTORM_CODES)
        peak_cape = max(cape[start:end], default=0.0)
        total_precip = sum(precip[start:end])
        peak_gust = max(gust[start:end], default=0.0)
        score = storm_score(thunder_hours, peak_cape, total_precip, peak_gust)
        scores.append(score)

        date = dt.date.fromisoformat(daily_time[day])
        english, chinese = day_label(day, date)

        headline_kind = kind_for_weathercode(
            daily_weathercode[day] if day < len(daily_weathercode) else weathercodes[start]
        )

        mean_humidity = (
            sum(humidity[start:end]) / HOURS_PER_DAY if humidity[start:end] else 0.0
        )

        days.append({
            "dateIso": daily_time[day],
            "dayEnglish": english,
            "dayChinese": chinese,
            "headlineSceneKind": headline_kind,
            "temperatureMaxC": round(daily_temp_max[day], 1) if day < len(daily_temp_max) else 0.0,
            "temperatureMinC": round(daily_temp_min[day], 1) if day < len(daily_temp_min) else 0.0,
            "humidityPercent": round(mean_humidity, 1),
            "windKmh": round(daily_wind_max[day], 1) if day < len(daily_wind_max) else 0.0,
            "rainChancePercent": round(daily_rain_chance[day], 1) if day < len(daily_rain_chance) else max(rain_chance_hourly[start:end], default=0.0),
            "thunderstormHours": thunder_hours,
            "peakCapeJkg": round(peak_cape, 1),
            "totalPrecipMm": round(total_precip, 2),
            "peakGustKmh": round(peak_gust, 1),
            "stormScore": round(score, 2),
            "segmentStartHours": [s for s, _ in segments],
            "segmentSceneKinds": [k for _, k in segments],
        })

    peak_index = max(range(len(scores)), key=lambda i: scores[i]) if scores else 0
    return days, peak_index


def summarise(days: list[dict], peak_index: int) -> None:
    print("  5-day outlook:")
    for i, day in enumerate(days):
        marker = " <- highest storm likelihood" if i == peak_index else ""
        print(
            f"    {day['dayEnglish']:>9} {day['dateIso']}  "
            f"score {day['stormScore']:>7.1f}  "
            f"thunder-hrs {day['thunderstormHours']}  "
            f"CAPE {day['peakCapeJkg']:>5.0f}  "
            f"rain {day['totalPrecipMm']:>5.1f}mm  "
            f"gust {day['peakGustKmh']:>4.0f}km/h{marker}"
        )
    if days[peak_index]["thunderstormHours"] == 0:
        print(
            "  note: no day in this window has a literal thunderstorm code — "
            "expected for a quiet week. The peak day above is the relatively "
            "most storm-prone real day, not a predicted storm."
        )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    try:
        import requests  # noqa: F401
    except ImportError as exc:
        print(f"Missing dependency: {exc}. Run: pip install -r tools/requirements.txt",
              file=sys.stderr)
        return 1

    bounds = geo.region_bounds()
    geo.ensure_dirs()

    print(f"Baking {FORECAST_DAYS}-day forecast for {bounds.center_lat:.4f}N "
          f"{bounds.center_lon:.4f}E")

    try:
        payload = fetch(bounds.center_lat, bounds.center_lon, args.timeout)
        days, peak_index = build_days(payload)
    except Exception as exc:  # noqa: BLE001 - the app falls back to the authored week
        print(f"  FAILED: {exc}", file=sys.stderr)
        print("  The app falls back to its authored demo week, so this is not fatal.",
              file=sys.stderr)
        return 1

    dataset = {
        "schemaVersion": SCHEMA_VERSION,
        "generatedUtc": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "source": "open-meteo",
        "attribution": ATTRIBUTION,
        "timezone": TIMEZONE,
        "centerLatitude": bounds.center_lat,
        "centerLongitude": bounds.center_lon,
        "peakStormDayIndex": peak_index,
        "days": days,
    }

    out_path = args.out or os.path.join(geo.DATA_DIR, "forecast.json")
    with open(out_path, "w", encoding="utf-8") as handle:
        json.dump(dataset, handle, separators=(",", ":"))

    print(f"  wrote {out_path} ({os.path.getsize(out_path) / 1024:.0f} KB)")
    summarise(days, peak_index)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

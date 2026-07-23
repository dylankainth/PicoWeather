"""Bake a weather snapshot to Assets/StreamingAssets/WeatherData/weather.json.

Two sources, one output schema:

* ``--source open-meteo`` (default) hits https://open-meteo.com. Free, no key,
  synchronous, and it exposes the variables the app needs.
* ``--source era5`` uses the Copernicus Climate Data Store, which is what the
  specification asks for. It needs a free CDS account and a ``~/.cdsapirc``,
  and its requests are queued -- a fetch can take minutes to hours. That is
  why it is not the runtime path, only a bake-time option.

Both writers emit the identical schema, so nothing downstream can tell which
one produced a file except by reading its ``source`` field.

Lightning: neither source publishes stroke density on a free tier, so
``lightning_potential`` is derived from CAPE and precipitation. It is labelled
as derived in the output and in the app.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import os
import sys

import geo

SCHEMA_VERSION = 1

OPEN_METEO_ENDPOINT = "https://api.open-meteo.com/v1/forecast"
OPEN_METEO_CURRENT = (
    "temperature_2m,precipitation,cloud_cover,cloud_cover_low,cloud_cover_mid,"
    "cloud_cover_high,wind_speed_10m,wind_direction_10m"
)
OPEN_METEO_ATTRIBUTION = (
    "Weather data by Open-Meteo.com (CC BY 4.0), ECMWF/DWD/NOAA source models."
)

SEA_LEVEL_PRESSURE_PA = 101325.0

# ECMWF's low/mid/high split, as pressure levels against a standard surface.
LAYER_PRESSURES_HPA = [
    ("low", 1000.0, 800.0),
    ("mid", 800.0, 450.0),
    ("high", 450.0, 200.0),
]


def altitude_from_pressure_hpa(pressure_hpa: float) -> float:
    """The specification's barometric formula: h = 44330 (1 - (P/P0)^(1/5.255))."""
    ratio = (pressure_hpa * 100.0) / SEA_LEVEL_PRESSURE_PA
    return 44330.0 * (1.0 - math.pow(ratio, 1.0 / 5.255))


def lightning_potential(cape_jkg: float, precip_mm_hr: float) -> float:
    """0..1 proxy for lightning activity. Mirrors Atmosphere.LightningPotential.

    CAPE below ~300 J/kg essentially never produces lightning; above ~2500 the
    atmosphere is strongly unstable. Precipitation gates it, because a dry
    unstable atmosphere is not a thunderstorm. Both factors are necessary,
    hence the product; the square root keeps moderate-but-real storms visible.
    """
    instability = min(max((cape_jkg - 300.0) / 2200.0, 0.0), 1.0)
    wetness = min(max(precip_mm_hr / 4.0, 0.0), 1.0)
    return min(max(math.sqrt(instability * wetness), 0.0), 1.0)


def build_layers() -> list[dict]:
    return [
        {
            "name": name,
            "basePressureHpa": base,
            "topPressureHpa": top,
            "baseAltitudeM": round(altitude_from_pressure_hpa(base), 1),
            "topAltitudeM": round(altitude_from_pressure_hpa(top), 1),
        }
        for name, base, top in LAYER_PRESSURES_HPA
    ]


def grid_points(bounds: geo.Bounds, size: int) -> list[tuple[float, float]]:
    """Row-major grid, x west->east and y south->north, matching WeatherDataset."""
    points = []
    for y in range(size):
        lat = bounds.min_lat + bounds.lat_span * y / (size - 1)
        for x in range(size):
            lon = bounds.min_lon + bounds.lon_span * x / (size - 1)
            points.append((lat, lon))
    return points


# --------------------------------------------------------------- Open-Meteo


def fetch_open_meteo(bounds: geo.Bounds, size: int, timeout: float) -> dict:
    import requests

    points = grid_points(bounds, size)
    params = {
        "latitude": ",".join(f"{lat:.4f}" for lat, _ in points),
        "longitude": ",".join(f"{lon:.4f}" for _, lon in points),
        "current": OPEN_METEO_CURRENT,
        "hourly": "cape",
        "forecast_days": 1,
        "timezone": "GMT",
    }

    print(f"  requesting {len(points)} locations from Open-Meteo...")
    response = requests.get(OPEN_METEO_ENDPOINT, params=params, timeout=timeout)
    response.raise_for_status()
    payload = response.json()

    # A multi-coordinate request returns a bare array; a single one returns an object.
    locations = payload if isinstance(payload, list) else [payload]
    if len(locations) != len(points):
        raise RuntimeError(
            f"expected {len(points)} locations in the response, got {len(locations)}"
        )

    now_hour = dt.datetime.now(dt.timezone.utc).hour
    cells = []
    observation_time = None

    for location in locations:
        current = location.get("current", {}) or {}
        if observation_time is None and current.get("time"):
            observation_time = current["time"] + "Z"

        precipitation = max(float(current.get("precipitation") or 0.0), 0.0)

        hourly = location.get("hourly", {}) or {}
        cape_series = hourly.get("cape") or []
        cape = 0.0
        if cape_series:
            index = min(now_hour, len(cape_series) - 1)
            cape = max(float(cape_series[index] or 0.0), 0.0)

        # Open-Meteo gives speed in km/h plus the meteorological direction (the
        # direction the wind blows *from*). The app wants eastward/northward
        # components, so flip the sign as well as decomposing.
        speed_ms = max(float(current.get("wind_speed_10m") or 0.0), 0.0) / 3.6
        from_rad = math.radians(float(current.get("wind_direction_10m") or 0.0))

        cells.append(
            {
                "cloudTotal": _fraction(current.get("cloud_cover")),
                "cloudLow": _fraction(current.get("cloud_cover_low")),
                "cloudMid": _fraction(current.get("cloud_cover_mid")),
                "cloudHigh": _fraction(current.get("cloud_cover_high")),
                "precipitationMmHr": round(precipitation, 3),
                "capeJkg": round(cape, 1),
                "lightningPotential": round(lightning_potential(cape, precipitation), 4),
                "temperatureC": round(float(current.get("temperature_2m") or 0.0), 2),
                "windU": round(-speed_ms * math.sin(from_rad), 3),
                "windV": round(-speed_ms * math.cos(from_rad), 3),
            }
        )

    return _assemble(bounds, size, cells, "open-meteo", OPEN_METEO_ATTRIBUTION, observation_time)


def _fraction(percent) -> float:
    return round(min(max(float(percent or 0.0) / 100.0, 0.0), 1.0), 4)


# --------------------------------------------------------------------- ERA5


def fetch_era5(bounds: geo.Bounds, size: int) -> dict:
    """Genuine ERA5 via the Copernicus CDS, for anyone with credentials.

    Requires ``pip install cdsapi xarray netcdf4`` and a ``~/.cdsapirc``. The
    request is queued server-side; expect minutes at best.
    """
    try:
        import cdsapi
        import numpy as np
        import xarray as xr
    except ImportError as exc:
        raise SystemExit(
            "ERA5 needs extra packages: pip install cdsapi xarray netcdf4\n"
            f"({exc})"
        ) from exc

    geo.ensure_dirs()
    target = os.path.join(geo.CACHE_DIR, "era5_shanghai.nc")

    # ERA5 lags real time by about five days, so ask for the most recent day
    # that is certain to exist rather than for today.
    day = dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=6)

    if not os.path.exists(target):
        print("  submitting an ERA5 request to the CDS (this queues; be patient)...")
        client = cdsapi.Client()
        client.retrieve(
            "reanalysis-era5-single-levels",
            {
                "product_type": "reanalysis",
                "format": "netcdf",
                "variable": [
                    "low_cloud_cover",
                    "medium_cloud_cover",
                    "high_cloud_cover",
                    "total_cloud_cover",
                    "total_precipitation",
                    "convective_available_potential_energy",
                    "2m_temperature",
                    "10m_u_component_of_wind",
                    "10m_v_component_of_wind",
                ],
                "year": f"{day.year:04d}",
                "month": f"{day.month:02d}",
                "day": f"{day.day:02d}",
                "time": "06:00",
                # CDS wants north/west/south/east.
                "area": [bounds.max_lat, bounds.min_lon, bounds.min_lat, bounds.max_lon],
            },
            target,
        )
    else:
        print(f"  reusing cached {target}")

    dataset = xr.open_dataset(target)
    first = {name: dataset[name].isel(time=0) for name in dataset.data_vars}

    lats = np.linspace(bounds.min_lat, bounds.max_lat, size)
    lons = np.linspace(bounds.min_lon, bounds.max_lon, size)

    def sample(name: str, default: float = 0.0):
        if name not in first:
            return np.full((size, size), default)
        return first[name].interp(latitude=("y", lats), longitude=("x", lons)).values

    # ERA5 names differ slightly between the netCDF and GRIB paths; try both.
    def pick(*names):
        for name in names:
            if name in first:
                return sample(name)
        return np.zeros((size, size))

    low = pick("lcc")
    mid = pick("mcc")
    high = pick("hcc")
    total = pick("tcc")
    # total_precipitation is accumulated metres over the hour -> mm/hour.
    precip = pick("tp") * 1000.0
    cape = pick("cape")
    temperature = pick("t2m") - 273.15
    wind_u = pick("u10")
    wind_v = pick("v10")

    cells = []
    for y in range(size):
        for x in range(size):
            p = float(max(precip[y][x], 0.0))
            c = float(max(cape[y][x], 0.0))
            cells.append(
                {
                    "cloudTotal": round(float(min(max(total[y][x], 0.0), 1.0)), 4),
                    "cloudLow": round(float(min(max(low[y][x], 0.0), 1.0)), 4),
                    "cloudMid": round(float(min(max(mid[y][x], 0.0), 1.0)), 4),
                    "cloudHigh": round(float(min(max(high[y][x], 0.0), 1.0)), 4),
                    "precipitationMmHr": round(p, 3),
                    "capeJkg": round(c, 1),
                    "lightningPotential": round(lightning_potential(c, p), 4),
                    "temperatureC": round(float(temperature[y][x]), 2),
                    "windU": round(float(wind_u[y][x]), 3),
                    "windV": round(float(wind_v[y][x]), 3),
                }
            )

    return _assemble(
        bounds,
        size,
        cells,
        "era5",
        "ERA5 reanalysis, Copernicus Climate Change Service (C3S).",
        day.strftime("%Y-%m-%dT06:00:00Z"),
    )


# ----------------------------------------------------------------- assembly


def _assemble(bounds, size, cells, source, attribution, observation_time) -> dict:
    now = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    return {
        "schemaVersion": SCHEMA_VERSION,
        "observationTimeUtc": observation_time or now,
        "generatedUtc": now,
        "source": source,
        "attribution": attribution,
        "minLatitude": bounds.min_lat,
        "maxLatitude": bounds.max_lat,
        "minLongitude": bounds.min_lon,
        "maxLongitude": bounds.max_lon,
        "gridWidth": size,
        "gridHeight": size,
        "layers": build_layers(),
        "cells": cells,
    }


def summarise(dataset: dict) -> None:
    cells = dataset["cells"]
    count = len(cells)
    mean_cloud = sum(c["cloudTotal"] for c in cells) / count
    mean_precip = sum(c["precipitationMmHr"] for c in cells) / count
    peak_cape = max(c["capeJkg"] for c in cells)
    peak_lightning = max(c["lightningPotential"] for c in cells)
    print(
        f"  cloud {mean_cloud * 100:.0f}% mean | rain {mean_precip:.2f} mm/h mean | "
        f"CAPE {peak_cape:.0f} J/kg peak | lightning potential {peak_lightning:.2f} peak"
    )
    if peak_lightning < 0.05:
        print("  note: this snapshot is quiet - expect few or no strikes in the app.")
        print("  For a demo storm, set ForceProceduralWeather on the AppConfig asset.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", choices=["open-meteo", "era5"], default="open-meteo")
    parser.add_argument("--grid", type=int, default=12,
                        help="grid cells per side (default 12)")
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--out", default=None, help="output path (default StreamingAssets)")
    args = parser.parse_args()

    if args.grid < 2:
        parser.error("--grid must be at least 2")

    bounds = geo.region_bounds()
    geo.ensure_dirs()

    print(f"Baking weather from {args.source} for {bounds.center_lat:.2f}N "
          f"{bounds.center_lon:.2f}E, {geo.SPAN_KM:.0f} km, {args.grid}x{args.grid} grid")

    try:
        if args.source == "era5":
            dataset = fetch_era5(bounds, args.grid)
        else:
            dataset = fetch_open_meteo(bounds, args.grid, args.timeout)
    except Exception as exc:  # noqa: BLE001 - report and fail cleanly, the app has a fallback
        print(f"  FAILED: {exc}", file=sys.stderr)
        print("  The app falls back to procedural weather, so this is not fatal.",
              file=sys.stderr)
        return 1

    out_path = args.out or os.path.join(geo.DATA_DIR, "weather.json")
    with open(out_path, "w", encoding="utf-8") as handle:
        json.dump(dataset, handle, separators=(",", ":"))

    size_kb = os.path.getsize(out_path) / 1024
    print(f"  wrote {out_path} ({size_kb:.0f} KB)")
    summarise(dataset)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

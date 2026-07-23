"""Bake every data layer into Assets/StreamingAssets/WeatherData/.

Runs the three fetchers and writes a manifest recording what actually landed.

None of the steps is required. Each layer the app cannot find, it generates
procedurally instead, so a failed bake degrades the demo rather than breaking
it -- which is why this script reports partial success clearly and still exits
zero unless *everything* failed.

    python tools/build_all.py
    python tools/build_all.py --skip satellite
    python tools/build_all.py --weather-source era5
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import subprocess
import sys

import geo

HERE = os.path.dirname(os.path.abspath(__file__))

STEPS = [
    ("terrain", "fetch_terrain.py", "terrain.bin"),
    ("satellite", "fetch_satellite.py", "satellite.jpg"),
    ("weather", "fetch_weather.py", "weather.json"),
]


def run_step(script: str, extra_args: list[str]) -> bool:
    command = [sys.executable, os.path.join(HERE, script), *extra_args]
    print(f"\n=== {script} " + "=" * (58 - len(script)))
    result = subprocess.run(command, cwd=HERE)
    return result.returncode == 0


def write_manifest(results: dict) -> str:
    bounds = geo.region_bounds()
    manifest = {
        "generatedUtc": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "region": {
            "name": "Shanghai",
            "centerLatitude": geo.CENTER_LAT,
            "centerLongitude": geo.CENTER_LON,
            "spanKm": geo.SPAN_KM,
            "minLatitude": bounds.min_lat,
            "maxLatitude": bounds.max_lat,
            "minLongitude": bounds.min_lon,
            "maxLongitude": bounds.max_lon,
        },
        "layers": results,
        "attribution": [
            "Elevation: AWS Terrain Tiles (elevation-tiles-prod), derived from SRTM "
            "and other public sources.",
            "Imagery: (c) Esri -- Maxar, Earthstar Geographics, and the GIS User "
            "Community.",
            "Weather: Open-Meteo.com (CC BY 4.0), ECMWF/DWD/NOAA source models.",
            "Lightning is not observed: it is derived from CAPE and precipitation "
            "rate. See Atmosphere.LightningPotential.",
        ],
    }

    path = os.path.join(geo.DATA_DIR, "manifest.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(manifest, handle, indent=2)
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skip", action="append", default=[],
                        choices=[name for name, _, _ in STEPS],
                        help="skip a layer (repeatable)")
    parser.add_argument("--terrain-resolution", type=int, default=512)
    parser.add_argument("--satellite-resolution", type=int, default=2048)
    parser.add_argument("--weather-grid", type=int, default=12)
    parser.add_argument("--weather-source", choices=["open-meteo", "era5"],
                        default="open-meteo")
    args = parser.parse_args()

    geo.ensure_dirs()
    print(f"Baking into {geo.DATA_DIR}")

    extra = {
        "terrain": ["--resolution", str(args.terrain_resolution)],
        "satellite": ["--resolution", str(args.satellite_resolution)],
        "weather": ["--grid", str(args.weather_grid),
                    "--source", args.weather_source],
    }

    results = {}
    attempted = 0
    succeeded = 0

    for name, script, filename in STEPS:
        if name in args.skip:
            results[name] = {"status": "skipped", "file": filename}
            continue

        attempted += 1
        ok = run_step(script, extra[name])
        path = os.path.join(geo.DATA_DIR, filename)
        exists = os.path.exists(path)

        if ok and exists:
            succeeded += 1
            results[name] = {
                "status": "ok",
                "file": filename,
                "bytes": os.path.getsize(path),
            }
        else:
            results[name] = {
                "status": "failed",
                "file": filename,
                "note": "the app will generate this layer procedurally",
            }

    manifest_path = write_manifest(results)

    print("\n" + "=" * 70)
    for name, info in results.items():
        marker = {"ok": "  ok  ", "failed": "FAILED", "skipped": " skip "}[info["status"]]
        size = f"{info['bytes'] / 1024:.0f} KB" if "bytes" in info else ""
        print(f"  [{marker}] {name:10} {info['file']:16} {size}")
    print(f"\n  manifest: {manifest_path}")

    if attempted and succeeded == 0:
        print("\n  Every layer failed. The app still runs, entirely procedurally.",
              file=sys.stderr)
        return 1

    if succeeded < attempted:
        print("\n  Some layers failed; the app will generate those procedurally.")

    print("\n  Next: in Unity, Tools > WeatherVR > Build Scene, then Build APK.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""Bake building footprints + heights to Assets/StreamingAssets/WeatherData/buildings.json.

Source: OpenStreetMap building footprints via the Overpass API, key-less and
free. This is what makes a city read as a city rather than a satellite photo
draped over a heightmap: London's relief across a 5 km tile is a couple of
metres, invisible at any honest scale, so the buildings *are* the terrain
layer's whole reason for existing here.

Height comes from whichever OSM tag is present, in order of preference:

    height                  -- metres, direct
    building:levels * 3 m   -- a standard storey-height estimate
    (untagged)              -- a small deterministic spread seeded from the
                               way's own OSM id, so an untagged building gets
                               a plausible height rather than a uniform box,
                               and the same id always gets the same height

Only simple ways tagged building=* are used; multipolygon relations (a
building with a courtyard hole) are skipped -- rare in this footprint, and
not worth a general polygon-with-holes path for a hackathon-scale bake.

Output is capped to --max-buildings by footprint area, largest first, so a
dense query still bounds the mesh the app has to build: the skyline's
landmarks survive the cut before its sheds do.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import sys

import geo

OVERPASS_URL = "https://overpass-api.de/api/interpreter"
METRES_PER_LEVEL = 3.0
DEFAULT_HEIGHT_BASE = 9.0
DEFAULT_HEIGHT_SPREAD = 18.0

ATTRIBUTION = "© OpenStreetMap contributors, ODbL 1.0."


def build_query(bounds: "geo.Bounds", timeout: int) -> str:
    # Overpass bbox order is (south, west, north, east) = (minLat, minLon, maxLat, maxLon).
    bbox = f"{bounds.min_lat},{bounds.min_lon},{bounds.max_lat},{bounds.max_lon}"
    return (
        f"[out:json][timeout:{timeout}];\n"
        f'way["building"]({bbox});\n'
        "out body;\n"
        ">;\n"
        "out skel qt;\n"
    )


def fetch_overpass(query: str, timeout: float) -> dict:
    """Runs the query, cached on disk by its own hash -- re-baking the same
    region twice should not hit the (rate-limited, shared) public endpoint."""
    import requests

    cache_key = hashlib.sha1(query.encode("utf-8")).hexdigest()[:16]
    cache_path = os.path.join(geo.CACHE_DIR, f"overpass_{cache_key}.json")
    if os.path.exists(cache_path):
        with open(cache_path, "r", encoding="utf-8") as handle:
            return json.load(handle)

    response = requests.post(
        OVERPASS_URL, data={"data": query}, timeout=timeout,
        headers={"User-Agent": "WeatherVR-bake/1.0"},
    )
    response.raise_for_status()
    data = response.json()

    with open(cache_path, "w", encoding="utf-8") as handle:
        json.dump(data, handle)
    return data


def parse_height(tags: dict, way_id: int) -> float:
    """First usable of: height tag, levels tag, deterministic default."""
    raw_height = tags.get("height")
    if raw_height:
        try:
            # OSM heights are occasionally "12 m" or "12m"; a plain float()
            # already handles "12", so only strip a trailing unit if present.
            return float(raw_height.strip().rstrip("m").strip())
        except ValueError:
            pass

    raw_levels = tags.get("building:levels")
    if raw_levels:
        try:
            return float(raw_levels) * METRES_PER_LEVEL
        except ValueError:
            pass

    # Deterministic, not random: the same building gets the same height on
    # every bake, so a demo does not visibly change shape between runs.
    jitter = (way_id * 2654435761) % 1000 / 1000.0  # Knuth multiplicative hash
    return DEFAULT_HEIGHT_BASE + jitter * DEFAULT_HEIGHT_SPREAD


def shoelace_area_signed(points: list[tuple[float, float]]) -> float:
    """Signed area in (lon, lat) space; positive is counter-clockwise."""
    total = 0.0
    n = len(points)
    for i in range(n):
        lon0, lat0 = points[i]
        lon1, lat1 = points[(i + 1) % n]
        total += lon0 * lat1 - lon1 * lat0
    return total * 0.5


def extract_buildings(payload: dict) -> list[dict]:
    nodes: dict[int, tuple[float, float]] = {}
    ways: list[dict] = []

    for element in payload.get("elements", []):
        kind = element.get("type")
        if kind == "node":
            nodes[element["id"]] = (element["lat"], element["lon"])
        elif kind == "way" and "building" in element.get("tags", {}):
            ways.append(element)

    buildings = []
    for way in ways:
        node_ids = way.get("nodes", [])
        # A closed ring repeats its first node as its last; drop the repeat.
        if len(node_ids) > 1 and node_ids[0] == node_ids[-1]:
            node_ids = node_ids[:-1]

        footprint = [nodes[i] for i in node_ids if i in nodes]
        if len(footprint) < 3:
            continue  # not enough points to be a polygon

        # footprint is (lat, lon); the area/winding check works in (lon, lat).
        lonlat = [(lon, lat) for lat, lon in footprint]
        area = shoelace_area_signed(lonlat)
        if area < 0:
            # Normalise to counter-clockwise. Local map space maps longitude
            # to +X and latitude to +Z with no reflection, so CCW here stays
            # CCW there -- BuildingMeshBuilder relies on that to wind its
            # walls and roof consistently without re-deriving it per mesh.
            footprint.reverse()

        height = parse_height(way.get("tags", {}), way["id"])

        buildings.append({
            "heightMeters": round(height, 2),
            "areaDegrees2": abs(area),
            "footprint": footprint,
        })

    return buildings


def write_buildings_json(path: str, bounds: "geo.Bounds", buildings: list[dict]) -> None:
    flat_buildings = []
    for building in buildings:
        flat = []
        for lat, lon in building["footprint"]:
            flat.append(lat)
            flat.append(lon)
        flat_buildings.append({
            "heightMeters": building["heightMeters"],
            "footprintFlat": flat,
        })

    payload = {
        "schemaVersion": 1,
        "source": "openstreetmap-overpass",
        "attribution": ATTRIBUTION,
        "generatedUtc": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "minLatitude": bounds.min_lat,
        "maxLatitude": bounds.max_lat,
        "minLongitude": bounds.min_lon,
        "maxLongitude": bounds.max_lon,
        "buildings": flat_buildings,
    }

    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--max-buildings", type=int, default=600,
                        help="cap on building count, largest footprint first (default 600)")
    parser.add_argument("--timeout", type=float, default=90.0)
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

    print(f"Baking buildings for {bounds.center_lat:.4f}N {bounds.center_lon:.4f}E, "
          f"{geo.SPAN_KM:.1f} km")

    try:
        query = build_query(bounds, int(args.timeout))
        payload = fetch_overpass(query, args.timeout)
        buildings = extract_buildings(payload)
        print(f"  {len(buildings)} building footprint(s) from Overpass")
    except Exception as exc:  # noqa: BLE001
        print(f"  FAILED: {exc}", file=sys.stderr)
        print("  The app falls back to procedural buildings, so this is not fatal.",
              file=sys.stderr)
        return 1

    if not buildings:
        print("  No buildings returned for this region; writing an empty file "
              "(the app will fall back to procedural).", file=sys.stderr)

    dropped = 0
    if len(buildings) > args.max_buildings:
        buildings.sort(key=lambda b: b["areaDegrees2"], reverse=True)
        dropped = len(buildings) - args.max_buildings
        buildings = buildings[:args.max_buildings]
        print(f"  capped to {args.max_buildings} largest footprints "
              f"({dropped} smaller building(s) dropped)")

    out_path = args.out or os.path.join(geo.DATA_DIR, "buildings.json")
    write_buildings_json(out_path, bounds, buildings)
    print(f"  wrote {out_path} ({os.path.getsize(out_path) / 1024:.0f} KB, "
          f"{len(buildings)} buildings)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

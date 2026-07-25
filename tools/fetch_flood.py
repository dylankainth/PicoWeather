"""Bake a hydraulic-connectivity field to Assets/StreamingAssets/WeatherData/flood.bin.

This is what turns the app's storm-surge overlay from a "bathtub" into something
defensible. The old overlay flooded every cell below a threshold, which is wrong
in two specific ways for a city:

* A hollow with no hydraulic path to the river floods anyway, even though no
  water could physically reach it.
* The Thames tidal defences -- the very things that decide whether the City of
  London floods at all -- are ignored, so the answer to "would a flood start
  here?" was "wherever the ground happens to be low", which is not the question
  anyone asks.

Both are fixed by computing, per cell, the **minimum water level at which water
can actually reach that cell from the river**, respecting real defence crest
heights as barriers. That is a minimax (bottleneck) shortest path from the water
seeds, which is exactly what priority-flood computes:

    connect[c] = min over paths seed->c of ( max effective_elevation along path )

Water standing at level L then floods cell c if and only if ``connect[c] <= L``,
and its depth there is ``L - dem[c]``. One number per cell answers it for every
level, so the runtime cost is a texture lookup rather than a flood fill.

Data sources, both key-less:

* **Defences** -- Environment Agency "Spatial Flood Defences (incl. standardised
  attributes)" via their ArcGIS FeatureServer. Crest height comes from
  ``actual_ucl`` (surveyed upper crest level) where present, falling back to
  ``design_ucl`` then ``effective_cl``. Open Government Licence.
* **Water seeds** -- OpenStreetMap via Overpass (``waterway``/``natural=water``),
  the same endpoint ``fetch_buildings.py`` already uses. ODbL.

Datum caveat, deliberately not silently ignored: EA crest levels are metres
above Ordnance Datum Newlyn, while the terrain is SRTM-derived (EGM96
orthometric). Over the UK those agree to well under a metre, which is inside
this bake's own resolution, but it is a real approximation and is recorded in
the output's provenance.

If the defence fetch fails the bake still succeeds -- it just produces an
undefended connectivity field, which is a *worse* answer but an honest one, and
it is recorded as such in the output.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import heapq
import json
import math
import os
import struct
import sys

import geo

MAGIC = b"PWFL"
VERSION = 1

TERRAIN_MAGIC = b"PWTR"

EA_DEFENCES_URL = (
    "https://environment.data.gov.uk/arcgis/rest/services/EA/"
    "SpatialFloodDefencesIncStandardisedAttributes/FeatureServer/0/query"
)
EA_PAGE_SIZE = 1000
EA_ATTRIBUTION = (
    "Flood defences: (c) Environment Agency copyright and/or database right "
    "2026. All rights reserved. Open Government Licence v3.0."
)

OVERPASS_URL = "https://overpass-api.de/api/interpreter"
OSM_ATTRIBUTION = "Water features: (c) OpenStreetMap contributors, ODbL 1.0."

# Crest fields in order of preference: a surveyed level beats a design level,
# and both beat the derived "effective" one.
CREST_FIELDS = ("actual_ucl", "design_ucl", "effective_cl")

# Cells whose connection level is more than this far above the terrain minimum
# are treated as "never floods" for our purposes -- the app's largest preset is
# +10 m, so 40 m of headroom is generous and keeps the 16-bit quantisation fine.
DEFAULT_RANGE_METERS = 40.0


# --------------------------------------------------------------- terrain input


class Heightfield:
    """The already-baked terrain.bin, read back so this step shares its exact grid."""

    def __init__(self, width, height, bounds, min_elevation, max_elevation, samples):
        self.width = width
        self.height = height
        self.bounds = bounds
        self.min_elevation = min_elevation
        self.max_elevation = max_elevation
        self.samples = samples  # normalised 0..65535, row-major, south row first

    @property
    def elevation_range(self) -> float:
        return max(self.max_elevation - self.min_elevation, 0.001)

    def elevation_at(self, index: int) -> float:
        return self.min_elevation + self.samples[index] / 65535.0 * self.elevation_range


def read_terrain_bin(path: str) -> Heightfield:
    with open(path, "rb") as handle:
        data = handle.read()

    if len(data) < 4 or data[:4] != TERRAIN_MAGIC:
        raise RuntimeError(f"{path} is not a PWTR heightfield")

    offset = 4
    (version,) = struct.unpack_from("<i", data, offset)
    offset += 4
    if version != 1:
        raise RuntimeError(f"{path} is version {version}, this script reads version 1")

    width, height = struct.unpack_from("<ii", data, offset)
    offset += 8
    min_lat, max_lat, min_lon, max_lon = struct.unpack_from("<dddd", data, offset)
    offset += 32
    min_elevation, max_elevation = struct.unpack_from("<ff", data, offset)
    offset += 8

    count = width * height
    samples = list(struct.unpack_from(f"<{count}H", data, offset))

    return Heightfield(
        width, height,
        geo.Bounds(min_lat, max_lat, min_lon, max_lon),
        min_elevation, max_elevation, samples,
    )


# ------------------------------------------------------------------ rasterising


def to_grid(field: Heightfield, lat: float, lon: float) -> tuple[int, int]:
    """Lat/lon to grid coordinates. Matches TerrainHeightfield.SampleElevation:
    u runs west->east, v runs south->north, and row 0 is the southern row."""
    u = (lon - field.bounds.min_lon) / field.bounds.lon_span
    v = (lat - field.bounds.min_lat) / field.bounds.lat_span
    x = int(round(u * (field.width - 1)))
    y = int(round(v * (field.height - 1)))
    return x, y


def line_cells(x0: int, y0: int, x1: int, y1: int):
    """Integer Bresenham. Defences and rivers are thin features on a coarse grid;
    a gap in a rasterised defence line is a hole water leaks through, so the
    line has to be 8-connected rather than sampled at the vertices."""
    dx = abs(x1 - x0)
    dy = abs(y1 - y0)
    sx = 1 if x0 < x1 else -1
    sy = 1 if y0 < y1 else -1
    err = dx - dy

    while True:
        yield x0, y0
        if x0 == x1 and y0 == y1:
            return
        err2 = 2 * err
        if err2 > -dy:
            err -= dy
            x0 += sx
        if err2 < dx:
            err += dx
            y0 += sy


def stamp_path(field: Heightfield, target: list, path: list, value: float,
               combine_max: bool = True) -> int:
    """Rasterises a lat/lon path into ``target``, returning cells touched."""
    touched = 0
    previous = None

    for lat, lon in path:
        current = to_grid(field, lat, lon)
        if previous is not None:
            for x, y in line_cells(previous[0], previous[1], current[0], current[1]):
                if 0 <= x < field.width and 0 <= y < field.height:
                    index = y * field.width + x
                    if combine_max:
                        if value > target[index]:
                            target[index] = value
                    else:
                        target[index] = value
                    touched += 1
        previous = current

    return touched


# ------------------------------------------------------------------ EA defences


def fetch_ea_defences(bounds: geo.Bounds, timeout: float) -> list[dict]:
    """Every EA flood-defence line intersecting the region, with its crest level.

    Paginated: the FeatureServer caps a response at 1000 features and flags
    ``exceededTransferLimit`` when there are more.
    """
    import requests

    cache_key = hashlib.sha1(
        f"{bounds.min_lat},{bounds.max_lat},{bounds.min_lon},{bounds.max_lon}".encode()
    ).hexdigest()[:16]
    cache_path = os.path.join(geo.CACHE_DIR, f"ea_defences_{cache_key}.json")
    if os.path.exists(cache_path):
        with open(cache_path, "r", encoding="utf-8") as handle:
            return json.load(handle)

    session = requests.Session()
    session.headers["User-Agent"] = "WeatherVR-bake/1.0"

    features: list[dict] = []
    offset = 0

    while True:
        params = {
            "where": "1=1",
            "geometry": f"{bounds.min_lon},{bounds.min_lat},{bounds.max_lon},{bounds.max_lat}",
            "geometryType": "esriGeometryEnvelope",
            "inSR": "4326",
            "outSR": "4326",
            "spatialRel": "esriSpatialRelIntersects",
            "outFields": ",".join(("asset_name", "protection_type", *CREST_FIELDS)),
            "returnGeometry": "true",
            "resultOffset": offset,
            "resultRecordCount": EA_PAGE_SIZE,
            "f": "geojson",
        }
        response = session.get(EA_DEFENCES_URL, params=params, timeout=timeout)
        response.raise_for_status()
        payload = response.json()

        page = payload.get("features") or []
        features.extend(page)

        if not page or not payload.get("exceededTransferLimit"):
            break
        offset += len(page)
        if offset > 20000:  # a 5 km tile with this many defences means a bad query
            break

    with open(cache_path, "w", encoding="utf-8") as handle:
        json.dump(features, handle)
    return features


def crest_level(properties: dict):
    """First usable crest level in metres AOD, or None if the asset has none."""
    for name in CREST_FIELDS:
        value = properties.get(name)
        if value is None:
            continue
        try:
            level = float(value)
        except (TypeError, ValueError):
            continue
        # EA uses sentinel-ish zeros and obvious nonsense for unsurveyed assets.
        if -50.0 < level < 200.0 and level != 0.0:
            return level
    return None


def geojson_paths(geometry: dict) -> list[list[tuple[float, float]]]:
    """GeoJSON LineString/MultiLineString to lists of (lat, lon)."""
    if not geometry:
        return []

    kind = geometry.get("type")
    coordinates = geometry.get("coordinates") or []

    if kind == "LineString":
        rings = [coordinates]
    elif kind == "MultiLineString":
        rings = coordinates
    elif kind == "Polygon":
        rings = coordinates
    elif kind == "MultiPolygon":
        rings = [ring for polygon in coordinates for ring in polygon]
    else:
        return []

    paths = []
    for ring in rings:
        path = [(point[1], point[0]) for point in ring if len(point) >= 2]
        if len(path) >= 2:
            paths.append(path)
    return paths


def close_defence_gaps(field: Heightfield, barriers: list, is_defence: bytearray) -> int:
    """Closes single-cell rasterisation gaps between adjacent defence assets.

    EA surveys and digitises each defence structure as its own separate
    feature. Two neighbouring assets that physically abut on the riverbank do
    not necessarily share an endpoint vertex once each is independently
    rasterised onto a ~10 m grid cell (this tile's 512 samples over 5 km) --
    Bresenham can leave the seam a cell wide. A pure minimax flood-fill treats
    that seam exactly like a real gap in the wall and pours through it, which
    is why the first bake of this tile showed the defences holding back real
    area at 3.5 m AOD but the effect vanishing by 4.5 m: the connectivity was
    leaking through seams, not overtopping actual crests.

    One pass of max-dilation restricted to cells adjacent to a real defence
    cell -- never spreading onto a cell with no surveyed asset at all -- closes
    a one-cell seam without inventing protection where none was surveyed.
    """
    width, height = field.width, field.height
    updates = {}

    for index in range(width * height):
        if is_defence[index]:
            continue
        y, x = divmod(index, width)
        best = None
        for dy in (-1, 0, 1):
            ny = y + dy
            if ny < 0 or ny >= height:
                continue
            for dx in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                nx = x + dx
                if nx < 0 or nx >= width:
                    continue
                neighbour = ny * width + nx
                if is_defence[neighbour]:
                    if best is None or barriers[neighbour] < best:
                        best = barriers[neighbour]
        # Only fills a gap flanked by defence on both sides at a comparable
        # level -- a cell with just one defended neighbour is the open end of
        # a wall, not a seam, and must stay open.
        if best is not None and best > barriers[index]:
            opposite_defended = False
            for dy in (-1, 0, 1):
                ny = y + dy
                if ny < 0 or ny >= height:
                    continue
                for dx in (-1, 0, 1):
                    if dx == 0 and dy == 0:
                        continue
                    nx = x + dx
                    if nx < 0 or nx >= width:
                        continue
                    neighbour = ny * width + nx
                    if is_defence[neighbour] and (
                        (dy == 1 and any(is_defence[(y - 1) * width + x2]
                                         for x2 in range(max(x - 1, 0), min(x + 2, width))
                                         if 0 <= y - 1 < height)) or
                        (dy == -1 and any(is_defence[(y + 1) * width + x2]
                                          for x2 in range(max(x - 1, 0), min(x + 2, width))
                                          if 0 <= y + 1 < height))
                    ):
                        opposite_defended = True
            if opposite_defended:
                updates[index] = best

    for index, value in updates.items():
        barriers[index] = value
    return len(updates)


def build_barriers(field: Heightfield, features: list[dict]) -> tuple[list, dict]:
    """Raises defence cells to their crest level.

    A defence is a barrier only to the extent that it stands above the ground it
    sits on, so the barrier grid holds ``max(terrain, crest)`` -- taking the
    crest alone would *lower* a wall built on high ground.
    """
    barriers = [0.0] * (field.width * field.height)
    for index in range(len(barriers)):
        barriers[index] = field.elevation_at(index)
    is_defence = bytearray(len(barriers))

    used = 0
    skipped_no_level = 0
    cells = 0
    levels = []

    for feature in features:
        level = crest_level(feature.get("properties") or {})
        if level is None:
            skipped_no_level += 1
            continue

        paths = geojson_paths(feature.get("geometry") or {})
        if not paths:
            continue

        for path in paths:
            previous = None
            for lat, lon in path:
                current = to_grid(field, lat, lon)
                if previous is not None:
                    for gx, gy in line_cells(previous[0], previous[1], current[0], current[1]):
                        if 0 <= gx < field.width and 0 <= gy < field.height:
                            gi = gy * field.width + gx
                            if level > barriers[gi]:
                                barriers[gi] = level
                            is_defence[gi] = 1
                            cells += 1
                previous = current
        used += 1
        levels.append(level)

    gaps_closed = close_defence_gaps(field, barriers, is_defence)

    stats = {
        "featuresTotal": len(features),
        "featuresWithCrest": used,
        "featuresWithoutCrest": skipped_no_level,
        "cellsRaised": cells,
        "gapsClosed": gaps_closed,
        "crestMin": round(min(levels), 2) if levels else None,
        "crestMax": round(max(levels), 2) if levels else None,
    }
    return barriers, stats


# ---------------------------------------------------------------- water seeds


def build_seed_query(bounds: geo.Bounds, timeout: int) -> str:
    bbox = f"{bounds.min_lat},{bounds.min_lon},{bounds.max_lat},{bounds.max_lon}"
    return (
        f"[out:json][timeout:{timeout}];\n"
        "(\n"
        f'  way["waterway"~"^(river|riverbank)$"]({bbox});\n'
        f'  way["waterway"="canal"]({bbox});\n'
        f'  way["natural"="water"]({bbox});\n'
        ");\n"
        "out body;\n"
        ">;\n"
        "out skel qt;\n"
    )


def classify_way(tags: dict) -> str:
    """Which flood source, if any, a mapped water feature represents.

    The distinction matters more than it looks. Tidal flooding in London comes
    from the Thames, and the defences exist to hold it there. Impounded water --
    St Katharine Docks, the canal basins, ornamental lakes -- sits *inland* of
    those defences behind locks, at a level that does not follow the tide. Seed
    the flood from a dock and water starts on the dry side of the wall, which
    makes the defences look useless and collapses the whole model back into a
    bathtub. Only ``river``/``riverbank`` is a tidal source here.
    """
    waterway = tags.get("waterway")
    if waterway in ("river", "riverbank"):
        return "river"
    if waterway == "canal":
        return "canal"
    if tags.get("natural") == "water":
        return "water"
    return "other"


def fetch_osm_water(bounds: geo.Bounds, timeout: float) -> list[tuple[str, list]]:
    """Returns (kind, path) pairs, kind as classified by :func:`classify_way`."""
    import requests

    query = build_seed_query(bounds, int(timeout))
    cache_key = hashlib.sha1(query.encode("utf-8")).hexdigest()[:16]
    cache_path = os.path.join(geo.CACHE_DIR, f"overpass_water_{cache_key}.json")

    if os.path.exists(cache_path):
        with open(cache_path, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
    else:
        response = requests.post(
            OVERPASS_URL, data={"data": query}, timeout=timeout,
            headers={"User-Agent": "WeatherVR-bake/1.0"},
        )
        response.raise_for_status()
        payload = response.json()
        with open(cache_path, "w", encoding="utf-8") as handle:
            json.dump(payload, handle)

    nodes = {}
    ways = []
    for element in payload.get("elements", []):
        if element.get("type") == "node":
            nodes[element["id"]] = (element["lat"], element["lon"])
        elif element.get("type") == "way":
            ways.append(element)

    result = []
    for way in ways:
        path = [nodes[i] for i in way.get("nodes", []) if i in nodes]
        if len(path) >= 2:
            result.append((classify_way(way.get("tags", {})), path))
    return result


def build_seeds(field: Heightfield, water: list[tuple[str, list]],
                allowed_kinds: tuple, fallback_percentile: float
                ) -> tuple[list[int], str, dict]:
    """Grid indices water enters the tile from.

    Prefers real OSM river geometry. With none (a network failure, or an inland
    tile with no mapped river) it falls back to the lowest cells in the tile,
    which is the same assumption the old bathtub made -- correct often enough to
    be useful, and recorded in the output so nobody mistakes it for surveyed
    hydrology.
    """
    counts: dict[str, int] = {}
    for kind, _ in water:
        counts[kind] = counts.get(kind, 0) + 1

    mask = [0.0] * (field.width * field.height)
    touched = 0
    for kind, path in water:
        if kind not in allowed_kinds:
            continue
        touched += stamp_path(field, mask, path, 1.0, combine_max=True)

    if touched:
        seeds = [i for i, value in enumerate(mask) if value > 0.0]
        if seeds:
            return seeds, "openstreetmap:" + "+".join(allowed_kinds), counts

    elevations = sorted(field.elevation_at(i) for i in range(field.width * field.height))
    cutoff_index = int(len(elevations) * fallback_percentile)
    cutoff = elevations[min(cutoff_index, len(elevations) - 1)]
    seeds = [i for i in range(field.width * field.height)
             if field.elevation_at(i) <= cutoff]
    return seeds, "lowest-terrain-fallback", counts


# ------------------------------------------------------------- priority flood


def priority_flood(field: Heightfield, barriers: list, seeds: list[int]) -> list[float]:
    """Minimum water level at which each cell becomes connected to a seed.

    Dijkstra with ``max`` in place of ``+``: the cost of a path is the highest
    barrier along it, and we want the path that minimises that maximum. Popping
    from a min-heap finalises cells in ascending connection level, so the first
    time a cell is settled it already holds its best possible value.
    """
    width, height = field.width, field.height
    count = width * height
    infinity = float("inf")

    connect = [infinity] * count
    settled = bytearray(count)
    heap = []

    for index in seeds:
        level = barriers[index]
        if level < connect[index]:
            connect[index] = level
            heap.append((level, index))

    heapq.heapify(heap)

    while heap:
        level, index = heapq.heappop(heap)
        if settled[index]:
            continue
        settled[index] = 1

        y, x = divmod(index, width)

        for dy in (-1, 0, 1):
            ny = y + dy
            if ny < 0 or ny >= height:
                continue
            for dx in (-1, 0, 1):
                if dx == 0 and dy == 0:
                    continue
                nx = x + dx
                if nx < 0 or nx >= width:
                    continue

                neighbour = ny * width + nx
                if settled[neighbour]:
                    continue

                # The bottleneck of the path so far, extended by this cell.
                candidate = level if level > barriers[neighbour] else barriers[neighbour]
                if candidate < connect[neighbour]:
                    connect[neighbour] = candidate
                    heapq.heappush(heap, (candidate, neighbour))

    return connect


# ------------------------------------------------------------------- output


def write_flood_bin(path: str, field: Heightfield, connect: list[float],
                    range_meters: float, provenance: dict) -> dict:
    base = field.min_elevation
    cap = base + range_meters

    reachable = [value for value in connect if value < float("inf")]
    stats = {
        "cells": len(connect),
        "reachableCells": len(reachable),
        "connectMin": round(min(reachable), 3) if reachable else None,
        "connectMax": round(max(reachable), 3) if reachable else None,
        "baseMeters": round(base, 3),
        "capMeters": round(cap, 3),
    }

    with open(path, "wb") as handle:
        handle.write(MAGIC)
        handle.write(struct.pack("<i", VERSION))
        handle.write(struct.pack("<ii", field.width, field.height))
        handle.write(struct.pack("<dddd",
                                 field.bounds.min_lat, field.bounds.max_lat,
                                 field.bounds.min_lon, field.bounds.max_lon))
        # The quantisation window. 65535 means "at or above the cap", i.e. never
        # floods at any level this app can request -- no separate sentinel needed
        # because the cap is far above the largest surge preset.
        handle.write(struct.pack("<ff", base, cap))

        samples = bytearray()
        span = max(cap - base, 0.001)
        for value in connect:
            if value >= cap:
                samples += struct.pack("<H", 65535)
            else:
                normalised = int(round((value - base) / span * 65535.0))
                samples += struct.pack("<H", max(0, min(65535, normalised)))
        handle.write(samples)

    sidecar = os.path.splitext(path)[0] + ".json"
    with open(sidecar, "w", encoding="utf-8") as handle:
        json.dump({**provenance, "field": stats}, handle, indent=2)

    return stats


# Absolute levels in metres AOD, chosen against the real numbers this tile
# reports rather than as round relative offsets. London's mean high water spring
# is around 3.5 m AOD and the EA defences here crest between about 5.2 and 8.8 m,
# so this ladder walks from "a high tide the walls hold" through "the lowest
# defences overtop" to "most of them do" -- which is the only range where the
# defence data changes the answer at all.
REPORT_LEVELS_AOD = (3.5, 4.5, 5.5, 6.5, 7.5)


def summarise(field: Heightfield, connect: list[float], stats: dict,
              defence_stats: dict, seed_source: str,
              levels=REPORT_LEVELS_AOD) -> None:
    total = len(connect)
    bathtub_elevations = [field.elevation_at(i) for i in range(total)]

    print(f"  seeds from {seed_source}")
    if defence_stats["featuresWithCrest"]:
        print(f"  defences: {defence_stats['featuresWithCrest']} with crest levels "
              f"({defence_stats['crestMin']}..{defence_stats['crestMax']} m AOD), "
              f"{defence_stats['cellsRaised']} grid cells raised")
    else:
        print("  defences: none usable — this field is UNDEFENDED")

    print("  flooded area, connected vs bathtub, by absolute water level:")
    for level in levels:
        flooded = sum(1 for value in connect if value <= level)
        # What a pure bathtub would have claimed, for comparison.
        bathtub = sum(1 for value in bathtub_elevations if value <= level)
        print(f"    {level:>4.1f} m AOD -> {flooded / total * 100:5.1f}% connected "
              f"({bathtub / total * 100:5.1f}% if bathtub, "
              f"{(bathtub - flooded) / total * 100:+5.1f} pp held back)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--terrain", default=None,
                        help="input terrain.bin (default StreamingAssets)")
    parser.add_argument("--out", default=None)
    parser.add_argument("--timeout", type=float, default=60.0)
    parser.add_argument("--range", type=float, default=DEFAULT_RANGE_METERS,
                        help="metres above the terrain minimum to quantise over "
                             f"(default {DEFAULT_RANGE_METERS:.0f})")
    parser.add_argument("--seed-percentile", type=float, default=0.02,
                        help="fallback seed cutoff as a fraction of cells, used only "
                             "when no OSM water is found (default 0.02)")
    parser.add_argument("--seed-kinds", default="river",
                        help="comma-separated OSM water kinds to seed the flood from: "
                             "river, canal, water (default river — canals and docks "
                             "are impounded and sit inland of the tidal defences, so "
                             "seeding them starts the flood behind the walls)")
    parser.add_argument("--no-defences", action="store_true",
                        help="skip the EA fetch and bake an undefended field")
    args = parser.parse_args()

    allowed_kinds = tuple(
        kind.strip() for kind in args.seed_kinds.split(",") if kind.strip()
    )
    if not allowed_kinds:
        parser.error("--seed-kinds needs at least one kind")

    try:
        import requests  # noqa: F401
    except ImportError as exc:
        print(f"Missing dependency: {exc}. Run: pip install -r tools/requirements.txt",
              file=sys.stderr)
        return 1

    geo.ensure_dirs()
    terrain_path = args.terrain or os.path.join(geo.DATA_DIR, "terrain.bin")

    if not os.path.exists(terrain_path):
        print(f"  FAILED: {terrain_path} not found. Run fetch_terrain.py first — this "
              "step computes connectivity over that exact grid.", file=sys.stderr)
        return 1

    try:
        field = read_terrain_bin(terrain_path)
    except Exception as exc:  # noqa: BLE001
        print(f"  FAILED: {exc}", file=sys.stderr)
        return 1

    print(f"Baking flood connectivity over {field.width}x{field.height} terrain, "
          f"{field.min_elevation:.1f}..{field.max_elevation:.1f} m")

    # --- defences (optional; failure degrades the answer, does not stop the bake)
    defence_features = []
    defence_error = None
    if not args.no_defences:
        try:
            defence_features = fetch_ea_defences(field.bounds, args.timeout)
            print(f"  {len(defence_features)} EA defence feature(s) intersecting the tile")
        except Exception as exc:  # noqa: BLE001
            defence_error = str(exc)
            print(f"  WARNING: EA defence fetch failed ({exc});"
                  " baking an UNDEFENDED field", file=sys.stderr)

    barriers, defence_stats = build_barriers(field, defence_features)

    # --- seeds
    water = []
    seed_error = None
    try:
        water = fetch_osm_water(field.bounds, args.timeout)
        print(f"  {len(water)} OSM water way(s)")
    except Exception as exc:  # noqa: BLE001
        seed_error = str(exc)
        print(f"  WARNING: OSM water fetch failed ({exc}); "
              "falling back to lowest terrain as seeds", file=sys.stderr)

    seeds, seed_source, water_counts = build_seeds(
        field, water, allowed_kinds, args.seed_percentile)
    if water_counts:
        breakdown = ", ".join(f"{count} {kind}" for kind, count in sorted(water_counts.items()))
        print(f"    water breakdown: {breakdown} (seeding from {'+'.join(allowed_kinds)})")
    print(f"  {len(seeds)} seed cell(s)")

    if not seeds:
        print("  FAILED: no seed cells at all; cannot compute connectivity",
              file=sys.stderr)
        return 1

    # --- the actual computation
    connect = priority_flood(field, barriers, seeds)

    provenance = {
        "schemaVersion": VERSION,
        "generatedUtc": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "method": "priority-flood minimax connectivity from water seeds, "
                  "with EA defence crest levels as barriers",
        "defended": bool(defence_stats["featuresWithCrest"]),
        "defences": defence_stats,
        "defenceError": defence_error,
        "seedSource": seed_source,
        "seedKinds": list(allowed_kinds),
        "seedCells": len(seeds),
        "seedError": seed_error,
        "waterWayCounts": water_counts,
        "attribution": [
            EA_ATTRIBUTION,
            OSM_ATTRIBUTION,
            "Terrain: AWS Terrain Tiles (elevation-tiles-prod), SRTM-derived.",
        ],
        "datumNote": "EA crest levels are metres above Ordnance Datum Newlyn; the "
                     "terrain is EGM96 orthometric. The two agree to well under a "
                     "metre over the UK, which is within this bake's resolution.",
        "limitations": "Static water level, not a hydrodynamic simulation: no flow "
                       "routing, no surge duration or timing, no drainage or sewer "
                       "capacity, no defence breach or failure modelling. Answers "
                       "'where can water reach at this level', not 'how a flood "
                       "would unfold'.",
    }

    out_path = args.out or os.path.join(geo.DATA_DIR, "flood.bin")
    stats = write_flood_bin(out_path, field, connect, args.range, provenance)

    print(f"  wrote {out_path} ({os.path.getsize(out_path) / 1024:.0f} KB)")
    print(f"  {stats['reachableCells']}/{stats['cells']} cells reachable at any level")
    summarise(field, connect, stats, defence_stats, seed_source)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

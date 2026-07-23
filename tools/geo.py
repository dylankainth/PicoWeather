"""Shared geography helpers for the WeatherVR bake scripts.

Web Mercator tile maths, the region definition, and the output paths. Kept in
one place so terrain and imagery are guaranteed to cover exactly the same
ground -- a half-tile disagreement between the heightfield and the basemap is
the kind of bug that is invisible in a thumbnail and glaring in VR.
"""

from __future__ import annotations

import math
import os
from dataclasses import dataclass

# --- the region -------------------------------------------------------------
# Shanghai, per the specification.
CENTER_LAT = 31.23
CENTER_LON = 121.47
SPAN_KM = 50.0

METERS_PER_DEGREE_LAT = 111_320.0

# --- output -----------------------------------------------------------------
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(REPO_ROOT, "Assets", "StreamingAssets", "WeatherData")
CACHE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "cache")


@dataclass(frozen=True)
class Bounds:
    """A lat/lon rectangle. Mirrors GeoBounds on the C# side."""

    min_lat: float
    max_lat: float
    min_lon: float
    max_lon: float

    @property
    def center_lat(self) -> float:
        return (self.min_lat + self.max_lat) / 2.0

    @property
    def center_lon(self) -> float:
        return (self.min_lon + self.max_lon) / 2.0

    @property
    def lat_span(self) -> float:
        return self.max_lat - self.min_lat

    @property
    def lon_span(self) -> float:
        return self.max_lon - self.min_lon


def region_bounds(
    center_lat: float = CENTER_LAT,
    center_lon: float = CENTER_LON,
    span_km: float = SPAN_KM,
) -> Bounds:
    """A region that is square *on the ground*, not square in degrees.

    A degree of longitude is only ~95 km at Shanghai's latitude versus 111 km
    for a degree of latitude, so using equal degree spans would stretch the map
    by 16% east-west. This is the same correction GeoBounds.FromCenterSpan
    applies on the C# side.
    """
    half_meters = span_km * 1000.0 / 2.0
    half_lat = half_meters / METERS_PER_DEGREE_LAT

    meters_per_degree_lon = METERS_PER_DEGREE_LAT * math.cos(math.radians(center_lat))
    meters_per_degree_lon = max(abs(meters_per_degree_lon), 1.0)
    half_lon = half_meters / meters_per_degree_lon

    return Bounds(
        center_lat - half_lat,
        center_lat + half_lat,
        center_lon - half_lon,
        center_lon + half_lon,
    )


# --- Web Mercator -----------------------------------------------------------


def lonlat_to_tile(lon: float, lat: float, zoom: int) -> tuple[float, float]:
    """Fractional slippy-map tile coordinates. Origin is the north-west corner."""
    lat = max(min(lat, 85.05112878), -85.05112878)
    n = 2.0**zoom
    x = (lon + 180.0) / 360.0 * n
    lat_rad = math.radians(lat)
    y = (1.0 - math.asinh(math.tan(lat_rad)) / math.pi) / 2.0 * n
    return x, y


def tile_to_lonlat(x: float, y: float, zoom: int) -> tuple[float, float]:
    """Inverse of :func:`lonlat_to_tile`."""
    n = 2.0**zoom
    lon = x / n * 360.0 - 180.0
    lat = math.degrees(math.atan(math.sinh(math.pi * (1.0 - 2.0 * y / n))))
    return lon, lat


def zoom_for_resolution(bounds: Bounds, target_pixels: int, tile_size: int = 256) -> int:
    """Smallest zoom whose mosaic is at least ``target_pixels`` across.

    Overshooting zoom costs tiles exponentially, so this picks the cheapest
    level that still meets the requested resolution, then the caller resamples
    down to exactly what it wants.
    """
    for zoom in range(1, 16):
        x0, _ = lonlat_to_tile(bounds.min_lon, bounds.max_lat, zoom)
        x1, _ = lonlat_to_tile(bounds.max_lon, bounds.min_lat, zoom)
        if (x1 - x0) * tile_size >= target_pixels:
            return zoom
    return 15


def tile_range(bounds: Bounds, zoom: int) -> tuple[int, int, int, int]:
    """Inclusive integer tile range covering the bounds: (x0, y0, x1, y1)."""
    x0f, y0f = lonlat_to_tile(bounds.min_lon, bounds.max_lat, zoom)
    x1f, y1f = lonlat_to_tile(bounds.max_lon, bounds.min_lat, zoom)
    return (
        int(math.floor(x0f)),
        int(math.floor(y0f)),
        int(math.floor(x1f)),
        int(math.floor(y1f)),
    )


def ensure_dirs() -> None:
    os.makedirs(DATA_DIR, exist_ok=True)
    os.makedirs(CACHE_DIR, exist_ok=True)

"""Bake a heightfield to Assets/StreamingAssets/WeatherData/terrain.bin.

Source: AWS Terrain Tiles (``elevation-tiles-prod``), in Terrarium PNG format.

The specification asks for SRTM 90 m, which is what these tiles are built from
-- but USGS/NASA SRTM downloads require an Earthdata login, and a bake step that
stops to ask for credentials is a bake step that does not get run. AWS serves
the same data key-less over HTTP, so this needs nothing but a network
connection.

Terrarium encodes elevation in the RGB channels:

    elevation_metres = (R * 256 + G + B / 256) - 32768

which gives 1/256 m precision over the full range of terrestrial elevation.

Output is the compact ``PWTR`` binary that TerrainHeightfield.FromBytes reads;
see that file for the exact layout.
"""

from __future__ import annotations

import argparse
import io
import math
import os
import struct
import sys

import geo

TERRARIUM_URL = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png"
TILE_SIZE = 256

MAGIC = b"PWTR"
VERSION = 1


def fetch_tile(session, zoom: int, x: int, y: int, timeout: float):
    """One tile, cached on disk. Missing tiles are ocean and decode to sea level."""
    from PIL import Image

    cache_path = os.path.join(geo.CACHE_DIR, f"terrarium_{zoom}_{x}_{y}.png")
    if os.path.exists(cache_path):
        return Image.open(cache_path).convert("RGB")

    url = TERRARIUM_URL.format(z=zoom, x=x, y=y)
    response = session.get(url, timeout=timeout)
    if response.status_code == 404:
        return None
    response.raise_for_status()

    with open(cache_path, "wb") as handle:
        handle.write(response.content)
    return Image.open(io.BytesIO(response.content)).convert("RGB")


def build_mosaic(bounds: geo.Bounds, zoom: int, timeout: float):
    """Stitches every tile covering the bounds into one RGB image."""
    import requests
    from PIL import Image

    x0, y0, x1, y1 = geo.tile_range(bounds, zoom)
    columns = x1 - x0 + 1
    rows = y1 - y0 + 1
    total = columns * rows

    print(f"  zoom {zoom}: {columns}x{rows} = {total} tiles")
    if total > 400:
        raise RuntimeError(
            f"{total} tiles is more than this script will politely request; "
            "lower --resolution"
        )

    mosaic = Image.new("RGB", (columns * TILE_SIZE, rows * TILE_SIZE), (128, 0, 0))
    session = requests.Session()
    session.headers["User-Agent"] = "WeatherVR-bake/1.0"

    fetched = 0
    for row, ty in enumerate(range(y0, y1 + 1)):
        for column, tx in enumerate(range(x0, x1 + 1)):
            tile = fetch_tile(session, zoom, tx, ty, timeout)
            if tile is not None:
                mosaic.paste(tile, (column * TILE_SIZE, row * TILE_SIZE))
                fetched += 1
        print(f"    row {row + 1}/{rows}", end="\r", flush=True)

    print(f"    fetched {fetched}/{total} tiles          ")
    return mosaic, (x0, y0, x1, y1)


def crop_to_bounds(mosaic, bounds: geo.Bounds, zoom: int, tiles):
    """Crops the tile mosaic to exactly the requested lat/lon rectangle."""
    x0, y0, x1, y1 = tiles

    left, top = geo.lonlat_to_tile(bounds.min_lon, bounds.max_lat, zoom)
    right, bottom = geo.lonlat_to_tile(bounds.max_lon, bounds.min_lat, zoom)

    box = (
        int(round((left - x0) * TILE_SIZE)),
        int(round((top - y0) * TILE_SIZE)),
        int(round((right - x0) * TILE_SIZE)),
        int(round((bottom - y0) * TILE_SIZE)),
    )
    return mosaic.crop(box)


def decode_full_resolution(image) -> tuple[list[float], int, int]:
    """Decodes Terrarium RGB to metres at the image's own resolution.

    Decoding happens *before* any resampling, deliberately. Terrarium packs
    elevation across three channels with the red channel worth 256 m per unit,
    so any filter with negative lobes -- Lanczos, bicubic -- overshoots at a
    coastline and turns a two-level RGB ripple into hundreds of metres of
    phantom terrain. Decode first, filter in metres, and that whole class of
    artefact cannot happen.

    Returns rows north-first, as the image is stored.
    """
    width, height = image.size
    raw = image.tobytes()  # tightly packed RGB, row-major

    elevations = [0.0] * (width * height)
    for i in range(width * height):
        offset = i * 3
        elevations[i] = (raw[offset] * 256.0 + raw[offset + 1] + raw[offset + 2] / 256.0) - 32768.0
    return elevations, width, height


def despike(elevations: list[float], width: int, height: int,
            tolerance: float = 35.0) -> int:
    """Replaces isolated spikes with the median of their neighbours.

    SRTM has well-known speckle: single-pixel voids and radar artefacts,
    especially over water and over tall urban structures. This bake of the
    Yangtze delta comes back with a handful of pixels at -189 m and +104 m
    against a genuine median of 4 m -- those are noise, and left alone they
    would set the normalisation range for the entire 16-bit heightfield,
    spending most of the precision on empty space and skewing every
    elevation-derived colour ramp in the app.

    The test is *local*, not against a global percentile gate. That distinction
    turned out to matter: gating on a global percentile flags every pixel of
    genuine high ground, because 98% of this tile is delta below 10 m, and the
    filter then flattened the real She Shan summit from 90 m to 51 m. What
    separates a spike from a summit is not its absolute height but whether its
    own neighbours corroborate it -- a hilltop sits just above the ring around
    it, a void sits nowhere near.

    Only pixels lying outside their neighbourhood's entire range are examined,
    so the cost is proportional to the number of spikes, not to the image.
    """
    replaced = 0

    for index, value in enumerate(elevations):
        y, x = divmod(index, width)

        neighbours = []
        for dy in (-1, 0, 1):
            ny = y + dy
            if ny < 0 or ny >= height:
                continue
            for dx in (-1, 0, 1):
                nx = x + dx
                if nx < 0 or nx >= width or (dx == 0 and dy == 0):
                    continue
                neighbours.append(elevations[ny * width + nx])

        if len(neighbours) < 5:
            continue  # image border: not enough context to judge

        lowest = min(neighbours)
        highest = max(neighbours)

        # A summit is at most one pixel's worth of slope above its ring; a void
        # is hundreds of metres outside it.
        if value > highest + tolerance or value < lowest - tolerance:
            neighbours.sort()
            elevations[index] = neighbours[len(neighbours) // 2]
            replaced += 1

    return replaced


def downsample(elevations: list[float], width: int, height: int,
               resolution: int) -> list[float]:
    """Area-averages down to ``resolution`` and flips to south-first rows.

    Box averaging rather than a resize: it is the correct reconstruction for a
    scalar field being decimated, it cannot overshoot, and it is what makes the
    90 m source honest at 100 m output spacing instead of merely sharp.
    """
    output = [0.0] * (resolution * resolution)

    for out_y in range(resolution):
        # Source rows are north-first, the output is south-first.
        source_y0 = int(out_y * height / resolution)
        source_y1 = max(int((out_y + 1) * height / resolution), source_y0 + 1)
        target_row = (resolution - 1 - out_y) * resolution

        for out_x in range(resolution):
            source_x0 = int(out_x * width / resolution)
            source_x1 = max(int((out_x + 1) * width / resolution), source_x0 + 1)

            total = 0.0
            samples = 0
            for sy in range(source_y0, min(source_y1, height)):
                base = sy * width
                for sx in range(source_x0, min(source_x1, width)):
                    total += elevations[base + sx]
                    samples += 1

            output[target_row + out_x] = total / samples if samples else 0.0

    return output


def write_terrain_bin(path: str, bounds: geo.Bounds, resolution: int,
                      elevations: list[float]) -> None:
    minimum = min(elevations)
    maximum = max(elevations)
    span = max(maximum - minimum, 0.001)

    with open(path, "wb") as handle:
        handle.write(MAGIC)
        handle.write(struct.pack("<i", VERSION))
        handle.write(struct.pack("<ii", resolution, resolution))
        handle.write(struct.pack("<dddd", bounds.min_lat, bounds.max_lat,
                                 bounds.min_lon, bounds.max_lon))
        handle.write(struct.pack("<ff", minimum, maximum))

        # 16-bit normalised: ~0.002 m precision over a 100 m range, far finer
        # than 90 m-class source data justifies, at half the size of float32.
        samples = bytearray()
        for elevation in elevations:
            normalised = int(round((elevation - minimum) / span * 65535.0))
            samples += struct.pack("<H", max(0, min(65535, normalised)))
        handle.write(samples)

    print(f"  elevation range {minimum:.1f} to {maximum:.1f} m")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--resolution", type=int, default=512,
                        help="output samples per side (default 512)")
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--out", default=None)
    parser.add_argument("--keep-spikes", action="store_true",
                        help="skip SRTM speckle removal (for inspecting raw data)")
    args = parser.parse_args()

    if args.resolution < 32:
        parser.error("--resolution must be at least 32")

    try:
        from PIL import Image  # noqa: F401
        import requests  # noqa: F401
    except ImportError as exc:
        print(f"Missing dependency: {exc}. Run: pip install -r tools/requirements.txt",
              file=sys.stderr)
        return 1

    bounds = geo.region_bounds()
    geo.ensure_dirs()

    print(f"Baking terrain for {bounds.center_lat:.2f}N {bounds.center_lon:.2f}E, "
          f"{geo.SPAN_KM:.0f} km, {args.resolution}x{args.resolution}")

    try:
        zoom = geo.zoom_for_resolution(bounds, args.resolution, TILE_SIZE)
        mosaic, tiles = build_mosaic(bounds, zoom, args.timeout)
        cropped = crop_to_bounds(mosaic, bounds, zoom, tiles)

        full, width, height = decode_full_resolution(cropped)
        print(f"  decoded {width}x{height} samples")

        if not args.keep_spikes:
            replaced = despike(full, width, height)
            print(f"  despiked {replaced} pixel(s) of SRTM speckle")

        elevations = downsample(full, width, height, args.resolution)
    except Exception as exc:  # noqa: BLE001
        print(f"  FAILED: {exc}", file=sys.stderr)
        print("  The app falls back to procedural terrain, so this is not fatal.",
              file=sys.stderr)
        return 1

    out_path = args.out or os.path.join(geo.DATA_DIR, "terrain.bin")
    write_terrain_bin(out_path, bounds, args.resolution, elevations)
    print(f"  wrote {out_path} ({os.path.getsize(out_path) / 1024:.0f} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

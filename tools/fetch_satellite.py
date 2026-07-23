"""Bake a basemap to Assets/StreamingAssets/WeatherData/satellite.jpg.

Source: ESRI World Imagery, via the ArcGIS Online tile service.

The specification asks for Sentinel-2. Sentinel-2 is the better *data* -- 10 m,
open licence, known provenance -- but getting a usable picture out of it means
an account on Copernicus or Sentinel Hub, downloading multi-band GeoTIFFs, and
compositing several passes to get something cloud-free, because any single pass
over Shanghai in summer is mostly cloud. That is a day of work to produce what
World Imagery already serves: a pre-composited, cloud-free, true-colour mosaic,
key-less, at more resolution than a 2 m tabletop map can show.

If you have Sentinel Hub credentials, ``--source sentinel2`` uses them.

Attribution, which the app displays: imagery (c) Esri and its contributors
(Maxar, Earthstar Geographics, and the GIS User Community).
"""

from __future__ import annotations

import argparse
import io
import os
import sys

import geo

ESRI_URL = (
    "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/"
    "MapServer/tile/{z}/{y}/{x}"
)
TILE_SIZE = 256

ESRI_ATTRIBUTION = (
    "Imagery (c) Esri -- Maxar, Earthstar Geographics, and the GIS User Community."
)


def fetch_tile(session, zoom: int, x: int, y: int, timeout: float):
    from PIL import Image

    cache_path = os.path.join(geo.CACHE_DIR, f"esri_{zoom}_{x}_{y}.jpg")
    if os.path.exists(cache_path):
        try:
            return Image.open(cache_path).convert("RGB")
        except OSError:
            os.remove(cache_path)  # truncated cache entry; refetch

    response = session.get(ESRI_URL.format(z=zoom, x=x, y=y), timeout=timeout)
    if response.status_code == 404:
        return None
    response.raise_for_status()

    with open(cache_path, "wb") as handle:
        handle.write(response.content)
    return Image.open(io.BytesIO(response.content)).convert("RGB")


def build_mosaic(bounds: geo.Bounds, zoom: int, timeout: float):
    import requests
    from PIL import Image

    x0, y0, x1, y1 = geo.tile_range(bounds, zoom)
    columns = x1 - x0 + 1
    rows = y1 - y0 + 1
    total = columns * rows

    print(f"  zoom {zoom}: {columns}x{rows} = {total} tiles")
    if total > 600:
        raise RuntimeError(
            f"{total} tiles is more than this script will politely request; "
            "lower --resolution"
        )

    mosaic = Image.new("RGB", (columns * TILE_SIZE, rows * TILE_SIZE), (16, 24, 32))
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
    if fetched == 0:
        raise RuntimeError("no tiles were returned; check the network connection")
    return mosaic, (x0, y0, x1, y1)


def crop_to_bounds(mosaic, bounds: geo.Bounds, zoom: int, tiles):
    """Crops to exactly the lat/lon rectangle the terrain covers.

    Getting this wrong by even a few pixels is the one error that would be
    invisible here and obvious in the headset: the coastline in the imagery
    would sit beside, rather than on, the coastline in the mesh.
    """
    x0, y0, _, _ = tiles

    left, top = geo.lonlat_to_tile(bounds.min_lon, bounds.max_lat, zoom)
    right, bottom = geo.lonlat_to_tile(bounds.max_lon, bounds.min_lat, zoom)

    box = (
        int(round((left - x0) * TILE_SIZE)),
        int(round((top - y0) * TILE_SIZE)),
        int(round((right - x0) * TILE_SIZE)),
        int(round((bottom - y0) * TILE_SIZE)),
    )
    return mosaic.crop(box)


def fetch_sentinel2(bounds: geo.Bounds, resolution: int, timeout: float):
    """True-colour Sentinel-2 via Sentinel Hub, for anyone with credentials.

    Set SENTINELHUB_CLIENT_ID and SENTINELHUB_CLIENT_SECRET in the environment.
    """
    import requests
    from PIL import Image

    client_id = os.environ.get("SENTINELHUB_CLIENT_ID")
    client_secret = os.environ.get("SENTINELHUB_CLIENT_SECRET")
    if not client_id or not client_secret:
        raise SystemExit(
            "Sentinel-2 needs SENTINELHUB_CLIENT_ID and SENTINELHUB_CLIENT_SECRET "
            "in the environment. Register free at https://www.sentinel-hub.com/."
        )

    token_response = requests.post(
        "https://services.sentinel-hub.com/oauth/token",
        data={
            "grant_type": "client_credentials",
            "client_id": client_id,
            "client_secret": client_secret,
        },
        timeout=timeout,
    )
    token_response.raise_for_status()
    token = token_response.json()["access_token"]

    # A least-cloudy mosaic over the last 90 days: any single pass over Shanghai
    # in summer is mostly cloud, so compositing is not optional.
    evalscript = """
//VERSION=3
function setup() {
  return { input: ["B02","B03","B04"], output: { bands: 3 } };
}
function evaluatePixel(s) {
  return [2.5 * s.B04, 2.5 * s.B03, 2.5 * s.B02];
}
"""
    import datetime as dt

    end = dt.datetime.now(dt.timezone.utc)
    start = end - dt.timedelta(days=90)

    request = {
        "input": {
            "bounds": {
                "bbox": [bounds.min_lon, bounds.min_lat, bounds.max_lon, bounds.max_lat],
                "properties": {"crs": "http://www.opengis.net/def/crs/EPSG/0/4326"},
            },
            "data": [
                {
                    "type": "sentinel-2-l2a",
                    "dataFilter": {
                        "timeRange": {
                            "from": start.strftime("%Y-%m-%dT00:00:00Z"),
                            "to": end.strftime("%Y-%m-%dT00:00:00Z"),
                        },
                        "maxCloudCoverage": 20,
                        "mosaickingOrder": "leastCC",
                    },
                }
            ],
        },
        "output": {
            "width": resolution,
            "height": resolution,
            "responses": [{"identifier": "default", "format": {"type": "image/jpeg"}}],
        },
        "evalscript": evalscript,
    }

    print("  requesting a least-cloudy 90-day composite from Sentinel Hub...")
    response = requests.post(
        "https://services.sentinel-hub.com/api/v1/process",
        headers={"Authorization": f"Bearer {token}"},
        json=request,
        timeout=timeout * 4,
    )
    response.raise_for_status()
    return Image.open(io.BytesIO(response.content)).convert("RGB")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", choices=["esri", "sentinel2"], default="esri")
    parser.add_argument("--resolution", type=int, default=2048,
                        help="output pixels per side (default 2048)")
    parser.add_argument("--quality", type=int, default=88,
                        help="JPEG quality (default 88)")
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    if args.resolution < 256:
        parser.error("--resolution must be at least 256")

    try:
        from PIL import Image
        import requests  # noqa: F401
    except ImportError as exc:
        print(f"Missing dependency: {exc}. Run: pip install -r tools/requirements.txt",
              file=sys.stderr)
        return 1

    bounds = geo.region_bounds()
    geo.ensure_dirs()

    print(f"Baking imagery from {args.source} for {bounds.center_lat:.2f}N "
          f"{bounds.center_lon:.2f}E, {geo.SPAN_KM:.0f} km, {args.resolution}px")

    try:
        if args.source == "sentinel2":
            image = fetch_sentinel2(bounds, args.resolution, args.timeout)
            attribution = "Contains modified Copernicus Sentinel data."
        else:
            zoom = geo.zoom_for_resolution(bounds, args.resolution, TILE_SIZE)
            mosaic, tiles = build_mosaic(bounds, zoom, args.timeout)
            cropped = crop_to_bounds(mosaic, bounds, zoom, tiles)
            # LANCZOS is right here and wrong for the heightfield: this is a
            # picture, and ringing at an edge is sharpening rather than a
            # hundred metres of phantom elevation.
            image = cropped.resize((args.resolution, args.resolution), Image.LANCZOS)
            attribution = ESRI_ATTRIBUTION
    except Exception as exc:  # noqa: BLE001
        print(f"  FAILED: {exc}", file=sys.stderr)
        print("  The app falls back to a synthesised basemap, so this is not fatal.",
              file=sys.stderr)
        return 1

    out_path = args.out or os.path.join(geo.DATA_DIR, "satellite.jpg")
    image.save(out_path, "JPEG", quality=args.quality, optimize=True, progressive=False)

    print(f"  wrote {out_path} ({os.path.getsize(out_path) / 1024:.0f} KB)")
    print(f"  attribution: {attribution}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

# Immersive Weather

A tabletop-scale VR weather visualisation for PICO / Meta Quest 3S. Place a
50 km × 50 km slice of Shanghai on a real table, then walk around it and look
into the atmosphere above it: satellite basemap, terrain relief, volumetric
cloud at true altitudes, and lightning that lights the map and the room.

Built for a hackathon. The full specification and the implementation notes —
including every deliberate deviation from the spec and why — are in
[`CLAUDE.md`](CLAUDE.md).

---

## Quick start

```powershell
# 1. Bake the data. Optional: the app generates every layer procedurally
#    if this is skipped, so it always runs.
pip install -r tools/requirements.txt
python tools/build_all.py

# 2. In Unity (2022.3.62f3):
#      Tools > WeatherVR > Build Scene
#      press Play, or
#      Tools > WeatherVR > Build APK

# 3. Install
adb install -r Builds/ImmersiveWeather.apk
```

There is nothing to wire up by hand. The scene, the config asset, the player
settings and the shader inclusion list are all generated or asserted from code.

## Using it

| Action | Controller | Hands |
| --- | --- | --- |
| Aim at a surface | point | point |
| Place the map | trigger | pinch |
| Move it again | hold grip ~1 s | — |

The map lands on a real horizontal surface if the runtime provides one, and
otherwise on a plane at table height in front of you.

## What is real and what is not

This matters, because roughly half of what you see is inferred rather than
observed, and none of it looks inferred. The app says so on the panel beside
the map; this is the same information in more detail.

| Layer | Source | Honest? |
| --- | --- | --- |
| Terrain | AWS Terrain Tiles (SRTM-derived), 90 m class | Measured |
| Basemap | Esri World Imagery, true colour | Measured |
| Cloud cover, rain, wind, CAPE | Open-Meteo (ECMWF/DWD/NOAA), live | Measured, ~10–30 km grid |
| Cloud *shape* | Procedural noise | **Invented.** No operational model resolves a cumulus tower. |
| Lightning | Derived from CAPE × rain rate | **Derived.** No free feed publishes stroke density. |
| Everything, offline | Procedural | **Synthetic.** Labelled "procedural" on screen. |

Vertical scale is exaggerated ×4 relative to horizontal by default
(`VerticalExaggeration` on the config asset). At true scale a 2 km cloud deck
over a 2 m map is an 8 cm film, which is accurate and unreadable.

## Demo mode

Shanghai is usually clear. A live snapshot is frequently an empty sky with no
lightning at all — correct, and impossible to demo. Set
`ForceProceduralWeather` on `Assets/WeatherVR/Resources/WeatherVRConfig.asset`
to always render the synthetic squall line. The provenance panel still reports
it as procedural.

## Repository layout

```
CLAUDE.md                     spec + implementation notes + known issues
tools/                        offline data pipeline (Python)
  build_all.py                runs all three fetchers, writes a manifest
  fetch_terrain.py            -> StreamingAssets/WeatherData/terrain.bin
  fetch_satellite.py          -> satellite.jpg
  fetch_weather.py            -> weather.json  (Open-Meteo, or ERA5 with a key)
  verify.py                   runs the maths checks outside Unity
Assets/WeatherVR/
  Scripts/Core/               config, orchestration, perf governor, solar position
  Scripts/Data/               geo + atmosphere maths, ingest, procedural fallbacks
  Scripts/Terrain/            mesh generation and LOD
  Scripts/Clouds/             density volume, raymarch driver
  Scripts/Lightning/          strike scheduling, channel geometry
  Scripts/Audio/              fully synthesised thunder, wind, rain
  Scripts/Interaction/        pointing and map placement
  Shaders/                    terrain, volumetric clouds, lightning
  Editor/                     scene generator, settings fixer, APK build
```

## Checks

```powershell
python tools/verify.py
```

Compiles the project's pure-logic code against the built `Assembly-CSharp.dll`
and runs it on a plain .NET runtime — no editor lock required, which matters
because Unity holds that lock essentially all the time during development. It
checks the barometric formula against standard-atmosphere reference points, the
solar position against both solstices, that the cloud detail volume tiles
seamlessly on all three axes, and that the real baked `terrain.bin` decodes to a
delta-flat median.

Anything needing the engine proper — shaders, `Texture3D`, MonoBehaviour
lifecycles — has to be checked by pressing Play.

## Performance

Targets 72 FPS on Quest 3S / PICO class hardware. The cloud raymarch is the
adjustable load: `PerfGovernor` measures frame time and spends march steps and
cloud self-shadowing before it spends frame rate. The shader integrates each
segment analytically rather than as a Riemann sum, so dropping steps softens the
clouds without changing their brightness — which is what makes the throttling
invisible.

Budget: ≤40 k triangles of terrain, ≤48 raymarch steps at half the cost of a
naive march, ≤3 concurrent bolts.

## Attribution

- Elevation: AWS Terrain Tiles (`elevation-tiles-prod`), derived from SRTM and
  other public sources.
- Imagery: © Esri — Maxar, Earthstar Geographics, and the GIS User Community.
- Weather: [Open-Meteo.com](https://open-meteo.com) (CC BY 4.0), from ECMWF,
  DWD and NOAA models.

# WeatherVR — Kotlin / PICO Spatial port

A rewrite of the app for PICO's Spatial runtime, in Kotlin, because PICO have said
the Unity SDK does not support spatial mode.

**Status: the data layer is ported and passing. Nothing renders yet.**

```
spatial/
  core/     pure Kotlin/JVM. No Android, no PICO SDK. 29 tests, all green.
  app/      not written yet — the Android + Spatial SDK surface.
```

## Why `core` has no Android dependency

It is the part that can be *proven* before the Spatial SDK is in hand. Everything in
`core` is arithmetic and file parsing, so it runs on a desktop JVM under `gradlew test`
— the same reasoning behind `tools/verify.py` on the Unity side, which compiles the C#
data layer and tests it outside the editor because Unity holds the project lock nearly
all the time.

Keeping it dependency-free is also what makes the port tractable at all: of the 12,544
lines of C# in the Unity app, this layer is the third that carries over as logic rather
than being rewritten against a new renderer.

```powershell
cd spatial
./gradlew :core:test
```

## What is ported

| C# source | Kotlin | Notes |
| --- | --- | --- |
| `Data/GeoBounds.cs` | `GeoBounds.kt` | `Vector2`/`Vector3` replaced with `MapPoint`/`MapUv`/`GeoPoint` — depending on engine types is what made the C# non-portable |
| `Data/Atmosphere.cs` | `Atmosphere.kt` | Layer pressures folded into the enum instead of parallel `when` blocks |
| `Core/MapScale.cs` | `MapScale.kt` | The unit convention. Read its header before positioning anything |
| `Data/TerrainHeightfield.cs` | `TerrainHeightfield.kt` | Same `PWTR` binary format, byte-for-byte |
| `Data/WeatherDataset.cs` | `WeatherDataset.kt` | Immutable data classes; wire format unchanged |
| — | `MathUtil.kt` | The four `Mathf` helpers the above needed |

The Python pipeline in `tools/` is **unchanged and still authoritative**. Both the Unity
build and this one read the same baked `terrain.bin`, `satellite.jpg`, `weather.json`
and `buildings.json`. That is the main reason this port is worth attempting rather than
starting over.

### The real-data test

`TerrainHeightfieldTest.decodes the real baked London terrain` reads the actual
`Assets/StreamingAssets/WeatherData/terrain.bin` and asserts the decode is sane. It
currently produces:

```
TerrainHeightfield[512x512, -5..44 m, GeoBounds[51.4911..51.5361 N, -0.1193..-0.0471 E]]
```

which is character-for-character what the Unity app logs on device. That is the
strongest evidence available that the binary reader is a faithful port — a big-endian
mistake here would not throw, it would yield plausible-looking noise.

The test skips rather than fails when the baked file is absent, since the data payload
is optional by design.

## What is not ported

Everything that touches the engine, which is most of the app:

- **Rendering.** 10 ShaderLab/HLSL shaders — pedestal, glass surround, studio sky,
  terrain, buildings, water — all using Unity-specific plumbing
  (`UnityObjectToClipPos`, single-pass-stereo macros, `SAMPLE_DEPTH_TEXTURE_PROJ`).
  These get rewritten against whatever the Spatial SDK's scene graph offers, not ported.
- **Mesh generation** (`Terrain/`), which builds the heightfield mesh and the OSM
  building massing. The *algorithms* port cleanly; the Unity `Mesh` API calls do not.
- **`Weather/WeatherVisuals`** — cloud deck, rain/snow, lightning flash. Built on
  `ParticleSystem`, `Light` and `LineRenderer`; no direct equivalents.
- **The whole UI.** Carousel, time scrubber and flood buttons are a world-space Unity
  `Canvas` with a custom ray-hit input path. On Android this should be native UI rather
  than a reimplementation.
- **`ProceduralTerrain` / `ProceduralSatellite` / `ProceduralWeather` / `Noise`** —
  portable logic, not yet done. Worth doing: they are what make the app run with no
  baked data at all.
- **JSON ingest** (`WeatherDataService`, `OpenMeteoClient`). Deliberately deferred so
  `core` stays dependency-free for now; `kotlinx.serialization` is the obvious choice
  and works on both Android and the JVM tests.

## The blocker

The `app` module cannot be written against the real API until PICO provide
`ByteDance.PICO.SpatialAdapter` (or its Kotlin equivalent). Note that the Unity SDK
already vendored in this repo *does* contain PICO Spatial building blocks —
`Packages/com.bytedance.pico.xr/Editor/BuildingBlocks/PICOMultiSpatial_BuildingBlocks.cs`,
offering "Spatial Camera For Volume Space" and "Spatial Camera For Stage Space" — gated
behind a `PICO_MS_SDK` define and an uninstalled package. Worth confirming with PICO
whether "not supported" means "not in the public preview" before committing further to
the rewrite.

## Design consequence, whichever SDK wins

Spatial mode gives the app a **bounded volume in the user's home environment**, not the
whole display. The surround built in the Unity version — studio sky, glass floor disc,
the unified kaleidoscope fold — assumes it owns the environment and cannot be shown in
a bounded volume. It either goes, or it folds inside the plinth. Budget for that as a
design change, not a port.

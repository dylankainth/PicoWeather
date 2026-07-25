# WeatherVR — Kotlin / PICO Spatial port

A rewrite of the app for PICO's Spatial runtime, in Kotlin, because PICO have said
the Unity SDK does not support spatial mode.

**Status: data layer, mesh generation and load policy are ported and passing.
Nothing renders yet — that is the only part that needs the SDK.**

```
spatial/
  core/     pure Kotlin/JVM. No Android, no PICO SDK. 112 tests, all green.
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
| `Core/SolarPosition.cs` | `SolarPosition.kt` | `DateTime` → `Instant`, which removes the `DateTimeKind` ambiguity entirely |
| `Data/TerrainHeightfield.cs` | `TerrainHeightfield.kt` | Same `PWTR` binary format, byte-for-byte |
| `Data/WeatherDataset.cs` | `WeatherDataset.kt` | Immutable data classes; wire format unchanged |
| `Data/BuildingDataset.cs` | `BuildingDataset.kt` | Flat footprint array kept — it is the file format, not a `JsonUtility` workaround any more |
| `Data/Noise.cs` | `Noise.kt` | Line-for-line; `uint` → `UInt` preserves the wrapping hash exactly |
| `Data/ProceduralTerrain.cs` | `ProceduralTerrain.kt` | |
| `Data/ProceduralWeather.cs` | `ProceduralWeather.kt` | Clock injected instead of `DateTime.UtcNow` inline, so output is testable |
| `Data/ProceduralBuildings.cs` | `ProceduralBuildings.kt` | |
| `Data/ProceduralSatellite.cs` | `ProceduralSatellite.kt` | Returns raw RGB bytes, not a `Texture2D` — that return type was the only thing making it unportable |
| `Data/OpenMeteoClient.cs` | `OpenMeteo.kt` | **URL building and parsing only.** Networking is the caller's job; it was `UnityWebRequest` coupling that made this untestable before |
| `Data/WeatherDataService.cs` | `WeatherJson.kt`, `WeatherDataService.kt` | Decode/encode, plus the live→baked→procedural policy behind `AssetSource`/`HttpFetcher` seams |
| `Terrain/TerrainMeshBuilder.cs` | `TerrainMeshBuilder.kt` | Emits `MeshData` buffers instead of a Unity `Mesh` |
| `Terrain/BuildingMeshBuilder.cs` | `BuildingMeshBuilder.kt` | Same, plus the winding rules the facades depend on |
| — | `MeshData.kt` | Engine-neutral vertex/index buffers and `recalculateNormals` |
| — | `MathUtil.kt` | The `Mathf` helpers the above needed |

The Python pipeline in `tools/` is **unchanged and still authoritative**. Both the Unity
build and this one read the same baked `terrain.bin`, `satellite.jpg`, `weather.json`
and `buildings.json`. That is the main reason this port is worth attempting rather than
starting over.

### The real-data tests

Three tests read the **actual baked payload** out of the Unity project next door rather
than a fixture written to match the parser. A schema test against your own fixture only
proves you are self-consistent. These currently produce:

```
TerrainHeightfield[512x512, -5..44 m, GeoBounds[51.4911..51.5361 N, -0.1193..-0.0471 E]]
openstreetmap-overpass · 600 building(s)
open-meteo · 12×12 grid · 2026-07-24T12:30Z
```

The first line is character-for-character what the Unity app logs on device. That is the
strongest evidence available that the readers are faithful — a big-endian mistake in the
terrain parser would not throw, it would yield plausible-looking noise.

They skip rather than fail when the baked files are absent, since the data payload is
optional by design.

### Things the tests pinned down

Not bugs found in the C#, but behaviours worth stating before a rewrite quietly changes
them:

- **`VerticalExaggeration` is named backwards.** At its shipping 0.4 it *compresses*
  altitude to 40% of true scale. Pinned by a test asserting altitude renders shorter
  than an equal building height.
- **A 1×1 weather grid parses but is not valid.** `isValid` needs width and height > 1
  because a single point cannot be bilinearly sampled — so a successful parse of a
  single-coordinate Open-Meteo response still yields nothing renderable. A caller that
  only checks for an exception would draw an empty map.
- **`parse` must not clamp the grid size the way `buildUrl` does.** The clamp exists to
  keep the request URL sane; applying it to the response makes a legitimate
  single-coordinate reply fail as "expected 4, got 1". I introduced exactly this bug in
  the port and the tests caught it.
- **Locale.** Open-Meteo takes comma-separated coordinates, so a comma decimal separator
  silently doubles the coordinate count. Pinned by a test that runs under a German
  locale.

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
- **HTTP.** `OpenMeteo` builds the URL and parses the reply; something has to fetch the
  bytes in between. That belongs in `app`, where the platform's HTTP client is known.
- **Load orchestration.** `WeatherDataService`'s "try live, then baked, then procedural"
  sequence is Unity coroutines end to end. The *policy* is three lines; it is the
  async machinery around it that has to be rewritten.

One deliberate content decision left alone: `ProceduralTerrain` still generates a river
delta shaped like the Yangtze, from when the project targeted Shanghai, even though the
region is now London. Changing it is a design call, not a port — and making it inside a
port is how you lose the ability to tell a translation bug from a redesign.

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

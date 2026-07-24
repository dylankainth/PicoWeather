# Pico 6 VR Weather Visualization App

## Hackathon Project Specification

---

## 1. Executive Summary

**Project Name:** Immersive Weather Visualization

**Platform:** Meta Quest 3S (Pico 6) with Android APK

**Core Concept:** A spatial, immersive weather visualization system that renders satellite imagery, terrain, and volumetric 3D clouds on a tabletop-scale virtual map, allowing users to place and interact with a real-world-accurate weather snapshot of Shanghai.

**Key Innovation:** Combining satellite imagery, high-resolution terrain data, and volumetric cloud rendering with dynamic lighting and animated lightning effects to create an intuitive, spatial understanding of weather systems.

---

## 2. Problem Statement

Traditional 2D weather apps (phones, desktops, even flat AR overlays) fail to convey:

- **Spatial depth** of cloud formations and precipitation systems
- **Volumetric scale** of weather phenomena
- **Intuitive interaction** with weather data
- **Immersive engagement** that drives understanding

Weather professionals, enthusiasts, and the general public struggle to visualize complex atmospheric systems in three dimensions. VR solves this by placing weather *in space*, allowing users to walk around, examine, and understand meteorological data naturally.

---

## 3. Solution Overview

### 3.1 User Experience

1. **Launch:** User opens the app in Pico 6
2. **Placement:** Via hand gesture or controller, user places a flat, rectangular weather map on a physical surface (table, floor, wall)
3. **Visualization:** The map displays:
    - **Base Layer:** High-resolution satellite imagery + terrain mesh (Shanghai region)
    - **Overlay:** Volumetric, semi-transparent clouds positioned at real-world heights
    - **Dynamic Effects:** Lightning strikes with accompanying sound and ambient lighting shifts
4. **Interaction:** User can:
    - Walk around the map (spatial awareness)
    - Observe cloud formations, precipitation patterns
    - See lightning illuminate the scene in real-time

### 3.2 Technical Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Pico 6 (Android)                     │
│                                                         │
│  ┌──────────────────────────────────────────────────┐  │
│  │              Unity 2022 LTS / 2023               │  │
│  │           + Pico XR Plugin SDK                   │  │
│  │                                                  │  │
│  │  ┌─────────────────────────────────────────┐   │  │
│  │  │   Rendering Pipeline                    │   │  │
│  │  │  ├─ Terrain Mesh (SRTM 90m)            │   │  │
│  │  │  ├─ Satellite Basemap (Sentinel-2)     │   │  │
│  │  │  ├─ Volumetric Cloud Raymarching       │   │  │
│  │  │  ├─ Lightning Geometry + Animator      │   │  │
│  │  │  └─ Lighting System (dynamic)          │   │  │
│  │  └─────────────────────────────────────────┘   │  │
│  │                                                  │  │
│  │  ┌─────────────────────────────────────────┐   │  │
│  │  │   Data Pipeline (Runtime)               │   │  │
│  │  │  ├─ ERA5 Weather Data Fetch             │   │  │
│  │  │  ├─ Cloud Height Parsing                │   │  │
│  │  │  ├─ Precipitation Data                  │   │  │
│  │  │  └─ Lightning Strike Generation         │   │  │
│  │  └─────────────────────────────────────────┘   │  │
│  │                                                  │  │
│  └──────────────────────────────────────────────────┘  │
│                                                         │
└─────────────────────────────────────────────────────────┘

Data Sources (Cloud):
├─ GEBCO / SRTM 90m (Terrain Elevation)
├─ Sentinel-2 (Satellite Imagery)
├─ ERA5 Reanalysis (Cloud height, precipitation, lightning)
└─ Local Processing (Shader-based volumetric rendering)
```

---

## 4. Technical Deep Dive

### 4.1 Data Sources & Pipeline

#### Terrain Data: GEBCO / SRTM 90m

- **Resolution:** 90m global coverage
- **Format:** GeoTIFF or HGT binary
- **Use:** Mesh generation for Shanghai region (~50km × 50km)
- **Processing:**
    - Download tiles covering Shanghai (coordinates: 31.23°N, 121.47°E)
    - Parse elevation grid
    - Generate Unity Mesh with vertex colors for height-based shading
    - LOD (Level of Detail) optimization for Pico 6 performance

#### Satellite Imagery: Sentinel-2

- **Resolution:** 10m per pixel (free tier)
- **Format:** GeoTIFF, RGB bands (true color composite)
- **Use:** Textured basemap for terrain mesh
- **Processing:**
    - Request Sentinel Hub API or ESA Copernicus Hub
    - Composite latest cloud-free image of Shanghai
    - Generate 2K–4K texture atlas
    - Map UV coordinates to geographic coordinates

#### Weather Data: ERA5 Reanalysis

- **Source:** Copernicus Climate Data Store (CDS)
- **Resolution:** 31km global, hourly data
- **Variables:**
    - Cloud cover (0–100%, by pressure level)
    - Cloud-top pressure / height
    - Precipitation rate (rain, snow)
    - Lightning occurrence (stroke density, #/km²/hour)
- **Processing:**
    - Request current/recent data for Shanghai
    - Interpolate cloud heights to geographic grid
    - Parse precipitation rates by pixel
    - Identify lightning hotspots
- **Access:** Free API (requires free registration)

### 4.2 Rendering Pipeline

#### 4.2.1 Scene Setup

```
Scene Hierarchy:
├─ WorldRoot (origin at user position)
│  ├─ WeatherMap (parent for all weather data)
│  │  ├─ TerrainMesh (SRTM-based mesh, Sentinel-2 texture)
│  │  ├─ VolumetricClouds (shader-based raymarching volume)
│  │  ├─ LightningParticles (animated arcs + sound)
│  │  └─ AmbientLight (dynamic, reacts to lightning)
│  └─ AudioSource (thunder, ambient wind)
└─ XRRig (Pico tracking)
```

#### 4.2.2 Volumetric Cloud Rendering (Shader-based Raymarching)

**Approach:** GPU-accelerated raymarching with Perlin noise and ERA5 data

```glsl
// Pseudo-code: Volumetric cloud shader
float SampleDensity(vec3 worldPos) {
    // 1. Sample ERA5 cloud cover at this location/height
    float cloudCover = SampleERA5Texture(worldPos.xz, worldPos.y);

    // 2. Apply Perlin noise for visual detail
    float perlin = PerlinNoise3D(worldPos * scale);

    // 3. Combine: cloud presence * noise texture
    float density = cloudCover * perlin;

    // 4. Fade out at cloud boundaries
    density *= SmoothStep(cloudBase, cloudTop, worldPos.y);

    return clamp(density, 0.0, 1.0);
}

vec3 RaymarchClouds(vec3 rayOrigin, vec3 rayDir) {
    vec3 lightColor = vec3(0.0);
    float transmittance = 1.0;

    for (int step = 0; step < MARCH_STEPS; step++) {
        vec3 pos = rayOrigin + rayDir * stepDistance * step;
        float density = SampleDensity(pos);

        if (density > 0.01) {
            // Sample lighting (sun + dynamic lightning)
            vec3 sunLight = SunDirectionLighting(pos);
            vec3 lightColor += sunLight * density * transmittance;
            transmittance *= exp(-density * absorption);
        }
    }
    return lightColor;
}
```

**Performance Optimization:**

- Adaptive step count (fewer steps at distance)
- Early termination (transmittance < threshold)
- Downsampled rendering (half-res volumetric, upsampled)
- Temporal reprojection (reuse previous frames)

#### 4.2.3 Cloud Height Accuracy

ERA5 provides cloud layers (low, mid, high) with pressure levels. Conversion to height:

```
Altitude (m) ≈ 44330 × [1 - (P / P₀)^(1/5.255)]
Where:
  P = pressure at cloud level (Pa)
  P₀ = sea level pressure (101325 Pa)
```

**In-world scale:** For a tabletop map:

- Map horizontal extent: ~50km real-world = 2m in VR
- Vertical scale: 1:50 horizontal scaling applied (clouds @ 2km height = 4cm above table)
- User can observe full atmospheric profile without overwhelming scale

### 4.3 Lightning System

#### Strike Generation

1. **Data source:** ERA5 lightning occurrence data (strokes/km²/hour)
2. **Simulation:**
    - Grid-based probability map from ERA5
    - Generate random strikes at high-probability zones
    - Frequency matches real-world data (e.g., 5–20 strikes/minute in active cells)

#### Visual & Audio

1. **Geometry:** Animated branching arc (shader-animated bezier curves)
2. **Light:** Intense point light at strike location, pulsing over 200ms
3. **Audio:** Layered thunder sound (delay based on distance, doppler shift)
4. **Scene response:**
    - Ambient light flicker (room responds to strike)
    - All objects briefly illuminated by strike light

### 4.4 Interaction Model

#### Placement (MVP)

- **Method 1:** Controller raycast → trigger button to place
- **Method 2:** Hand pinch gesture → drag to position
- **Constraints:** Map snaps to horizontal surfaces (table detection)

#### Viewing

- Free roaming within tracked play space
- Weather map stays anchored to placement location
- User walks around, observes from different angles

#### Future Extensions

- Resize/rotate map via gesture
- Timeline scrubber (see forecast progression)
- Precipitation particle effects (rain falling through volumetric space)

---

## 5. Technology Stack

| Component | Technology | Rationale |
| --- | --- | --- |
| **Engine** | Unity 2022 LTS / 2023 | Mature VR support, Pico SDK integration |
| **VR Platform** | Meta Quest 3S (Pico) | Consumer-grade, strong hand tracking |
| **Rendering** | HDRP (High Definition RP) | Advanced lighting, volumetric effects |
| **Shaders** | HLSL/ShaderLab | GPU-accelerated raymarching |
| **Data Processing** | Python (offline) / C# (runtime) | Terrain/satellite preprocessing, ERA5 fetching |
| **Mesh Generation** | Unity ProBuilder / custom C# | Terrain mesh from SRTM data |
| **Audio** | FMOD or Unity Audio | Spatialized thunder, ambient soundscape |
| **Networking** | None (offline MVP) | Local data, no cloud requirements |

---

## 6. MVP Scope & Deliverables

### 6.1 What's Included (Hackathon MVP)

✅ **Static snapshot of Shanghai weather (current time)**

- Terrain mesh (50km × 50km region, ~31.2°N, 121.5°E)
- Satellite basemap (Sentinel-2 true color)
- Volumetric clouds rendered via raymarching
- Cloud heights derived from ERA5 data
- Animated lightning strikes with audio
- Dynamic ambient lighting response to lightning

✅ **User placement & interaction**

- Tabletop map placement via controller/gesture
- Free roaming observation
- Proper spatial tracking (Pico 6 hand/controller)

✅ **Performance optimization**

- Optimized for Pico 6 hardware (target 72 FPS)
- Downsampled volumetric rendering
- LOD terrain mesh

### 6.2 Out of Scope (Post-Hackathon)

- Multi-location support / timezone handling
- Live-updating weather feeds
- Forecast timeline (animated progression)
- Precipitation particle systems
- Detailed meteorologist-level analytics
- Cloud type classification (cirrus, cumulus, etc.)
- Mobile app (iOS/Android 2D companion)

---

## 7. Implementation Plan (48–72 hour hackathon)

### Phase 1: Data Prep (6–8 hours)

- [ ]  Download SRTM 90m DEM for Shanghai region
- [ ]  Obtain Sentinel-2 RGB composite
- [ ]  Fetch ERA5 data (cloud cover, heights, lightning)
- [ ]  Python scripts to parse & export as Unity-compatible formats

**Deliverable:** `terrain.mesh`, `satellite.png`, `era5_clouds.json`

### Phase 2: Unity Scene Setup (4–6 hours)

- [ ]  Create base scene with XR rig (Pico SDK)
- [ ]  Import terrain mesh, apply satellite texture
- [ ]  Set up camera & lighting
- [ ]  Implement controller/hand input system

**Deliverable:** Playable scene, user can see terrain

### Phase 3: Volumetric Cloud Rendering (8–12 hours)

- [ ]  Shader implementation (raymarching + Perlin noise)
- [ ]  ERA5 data -> 3D texture (cloud density volume)
- [ ]  Camera depth-based sampling
- [ ]  Performance profiling & optimization (72 FPS target)

**Deliverable:** Volumetric clouds visible, accurate heights

### Phase 4: Lightning & Dynamics (4–6 hours)

- [ ]  Lightning strike spawning (grid-based, ERA5-driven)
- [ ]  Arc geometry + animation shader
- [ ]  Point light + flicker logic
- [ ]  Thunder audio + spatial audio setup

**Deliverable:** Lightning strikes with sound & scene response

### Phase 5: Polish & Testing (4–8 hours)

- [ ]  Performance optimization (profiler, memory usage)
- [ ]  Hand tracking refinement
- [ ]  Map placement UX polish
- [ ]  Bug fixes & edge cases

**Deliverable:** Stable, optimized MVP ready for demo

**Total: ~26–40 hours of focused development**

---

## 8. Technical Challenges & Mitigations

| Challenge | Impact | Mitigation |
| --- | --- | --- |
| **Pico 6 GPU limitations** | Volumetric raymarching may be slow | Adaptive quality, temporal reprojection, downsampled rendering |
| **ERA5 data access** | Requires API registration, data lag | Pre-download latest snapshot, cache locally |
| **Mesh generation complexity** | Terrain mesh from raw DEM requires careful optimization | Use Unity ProBuilder initially, optimize later; LOD system |
| **Shader complexity** | Raymarching shader difficult to debug on mobile | Start with simple noise, iterate; use RenderDoc for profiling |
| **Hand tracking instability** | Gesture placement might feel imprecise | Fallback to controller raycast; snap-to-surface |
| **Audio spatialization** | Thunder spatial audio setup complex | Use Unity Audio with distance attenuation; FMOD if time permits |

---

## 9. Success Criteria

### Must-Have (MVP)

- [ ]  Terrain + satellite basemap rendered correctly
- [ ]  Volumetric clouds visible at realistic heights
- [ ]  Lightning strikes animated with accompanying sound
- [ ]  Runs at ≥60 FPS on Pico 6 hardware
- [ ]  User can place map and walk around it
- [ ]  Demo-ready (stable for 5–10 min showcase)

### Nice-to-Have

- [ ]  72 FPS locked performance
- [ ]  Precipitation visualization
- [ ]  Advanced lightning effects (branching, multiple strokes)
- [ ]  Ambient soundscape (wind, rain audio texture)

---

## 10. Demo Script (2–3 min walkthrough)

```
1. Headset on, user sees empty VR space
2. Controller raycast to table → place weather map (Shanghai lighting up)
3. "Here's what Shanghai's weather looks like in 3D..."
   → Point to terrain, show satellite imagery detail
   → Look up at volumetric clouds, explain heights (real-world meters)
4. "Watch as lightning strikes in real-time..."
   → Strike animation with light pulse and thunder
   → Scene momentarily illuminated by strike
5. Walk around the map, observe from different angles
6. "This combines real satellite data, terrain, and weather models
    all in VR—giving meteorologists and enthusiasts a spatial
    understanding of atmospheric systems."
```

---

## 11. Resources & References

### Data Sources

- **GEBCO/SRTM:** https://www.usgs.gov/centers/eros/science/usgs-eros-archive-digital-elevation-shuttle-radar-topography-mission-srtm-1-arc
- **Sentinel-2:** https://scihub.copernicus.eu/ (or ESA Copernicus Hub)
- **ERA5 Reanalysis:** https://cds.climate.copernicus.eu/ (Copernicus Climate Data Store)

### Development

- **Pico XR Plugin for Unity:** https://developer-eu.pico.technology/
- **Unity HDRP Volumetrics:** https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@latest/index.html
- **Raymarching Tutorials:** Shadertoy (search "volumetric clouds")

### Inspiration

- Weather Underground (3D radar)
- NOAA Visualization Lab
- Windy.com (mobile app design reference)

---

## 12. Team Roles (Recommended)

- **Data Engineer:** SRTM/Sentinel-2/ERA5 pipeline, preprocessing
- **Graphics Programmer:** Shader development, volumetric rendering
- **XR/Gameplay:** Pico SDK integration, interaction, placement logic
- **Audio/Polish:** Lightning effects, sound design, performance optimization

---

## 13. Post-Hackathon Roadmap

### Week 1–2

- Multi-location support (user selects city)
- Forecast timeline (scrubber to see next 48 hours)
- Precipitation particle effects

### Month 1–2

- Live weather data feeds (hourly updates)
- Mobile 2D companion app
- Cloud type classification (cirrus, stratus, etc.)
- Meteorologist mode (add isobars, front lines, wind vectors)

### Month 3+

- Multiplayer (shared VR space)
- Professional tier (advanced analytics)
- Global weather monitoring dashboard
- AR mode (overlay on real world via Pico's camera)

---

**End of Specification Document**

*Generated for Hackathon MVP — Contact [Team] for updates or technical questions.*

---
---

# PART II — IMPLEMENTATION NOTES (living document)

This section records how the spec above is *actually* being realised in this
repository, including deliberate deviations. Read this before editing code.

## Environment (verified)

| Item | Value |
| --- | --- |
| Unity | **2022.3.62f3** (LTS) |
| Render pipeline | **Built-in RP** (no URP/HDRP package installed) |
| XR | PICO Unity Integration SDK **0.13.1-Preview** (`com.bytedance.pico.xr`, vendored in-repo at `Packages/com.bytedance.pico.xr/`, referenced from `Packages/manifest.json` as `file:com.bytedance.pico.xr` — a relative path, portable across machines) |
| Interaction | XR Interaction Toolkit 2.x, Unity Input System |

The original handover machine referenced the SDK by absolute path
(`C:/Users/dylan/Downloads/PICO-Unity-SDK-0.13.1-Preview/XR`), which does not
exist on other machines — opening the project there fails to resolve the
package. Fixed by vendoring the SDK folder directly into `Packages/` and
pointing `manifest.json` at it with a relative `file:` reference instead, so
the project opens on any machine with the repo checked out. Confirmed clean
on a second machine: `unity_open.log` shows a batch-mode open/close with no
compiler errors and no missing-package errors.

## Deliberate deviations from the spec

1. **HDRP → Built-in RP.** HDRP is not a viable target for standalone
   Android XR (tile-based mobile GPU, no deferred/volumetric budget). The
   project ships with Built-in RP and a hand-written raymarching shader that
   renders the cloud volume as a transparent box. This costs us HDRP's
   built-in volumetrics but is the only option that hits 72 FPS on device.
2. **ERA5 → Open-Meteo for the live path.** ERA5 via the Copernicus CDS API
   requires a registered account plus an async queued request that can take
   minutes to hours — unusable inside an app launch. Open-Meteo
   (`api.open-meteo.com`) is free, key-less, and exposes the same physical
   variables we need (`cloud_cover_low/mid/high`, `precipitation`, `cape`,
   `pressure_msl`, `temperature_2m`, wind). The offline preprocessing script
   still supports ERA5/CDS for users who have a key; both paths write the
   *same* `weather.json` schema, so nothing downstream changes.
3. **Lightning is derived, not measured.** Neither ERA5 hourly single-levels
   nor Open-Meteo expose stroke density on the free tier. Strike probability
   per cell is derived from CAPE × precipitation rate using a calibrated
   curve (see `tools/fetch_weather.py::lightning_potential`). This is a
   physically-motivated proxy, and the app labels it as such.
4. **SRTM → AWS Terrain Tiles (Terrarium).** SRTM downloads require a NASA
   Earthdata login. AWS's `elevation-tiles-prod` bucket serves the same
   SRTM-derived data as key-less Terrarium PNG tiles. Same 90 m-class
   resolution over Shanghai, no auth.
5. **Sentinel-2 → ESRI World Imagery tiles.** Sentinel Hub needs an account
   and returns raw multi-band GeoTIFFs requiring cloud-free compositing.
   ESRI World Imagery is a pre-composited, cloud-free, key-less true-colour
   basemap at the resolution we need for a 2 m tabletop map. The script
   retains a Sentinel-2 path for users with credentials.
6. **Everything degrades to procedural.** Every data fetch has a
   deterministic procedural fallback so the app *always* runs, even with no
   network and no baked data. This is a hackathon-demo safety net.

## Repository layout

```
My project (2)/
├─ CLAUDE.md                      ← this file
├─ tools/                         ← offline Python data pipeline
│  ├─ requirements.txt
│  ├─ fetch_terrain.py            → Assets/StreamingAssets/WeatherData/terrain.bin
│  ├─ fetch_satellite.py          → Assets/StreamingAssets/WeatherData/satellite.jpg
│  ├─ fetch_weather.py            → Assets/StreamingAssets/WeatherData/weather.json
│  └─ build_all.py                → runs all three
└─ Assets/
   ├─ StreamingAssets/WeatherData/   ← baked data payload (read at runtime)
   ├─ Scenes/WeatherVR.unity         ← the demo scene (generated by SceneBuilder)
   └─ WeatherVR/
      ├─ Scripts/
      │  ├─ Data/         WeatherDataset, GeoBounds, TerrainHeightfield, loaders
      │  ├─ Terrain/      TerrainMeshBuilder, LOD
      │  ├─ Clouds/       CloudVolumeBuilder (3D texture), CloudRenderer
      │  ├─ Lightning/    LightningDirector, LightningBolt, ThunderAudio
      │  ├─ Interaction/  MapPlacementController, MapAnchor
      │  ├─ Audio/        AmbientSoundscape, ProceduralAudio
      │  └─ Core/         WeatherSceneController, AppConfig, PerfGovernor
      ├─ Shaders/         VolumetricClouds.shader, LightningBolt.shader, Terrain*.shader
      ├─ Materials/
      └─ Editor/          SceneBuilder.cs, BuildAPK.cs, DataBakeWindow.cs
```

## Coordinate & scale conventions

- **Region:** City of London skyscraper cluster, centre `51.5136°N, -0.0832°E`,
  5 km × 5 km footprint (`AppConfig.RegionSpanKm`). Originally Shanghai at
  50 km × 50 km — see "Deliberate deviations" §2 above for why it shrank.
- **Map size in VR:** 2.0 m across (X/Z). So **1 VR metre = 2.5 km real**
  (`Horizontal = MapSizeMeters / RegionSpanMeters`, i.e. 1:2 500).
- **Vertical exaggeration is two knobs, both scaled to `RegionSpanKm`, not
  absolute constants:**
  - `AppConfig.VerticalExaggeration` (0.4) sets cloud/atmosphere altitude
    scale: `Vertical = Horizontal × VerticalExaggeration`. A 2 km cloud sits
    `(2000 − AtmosphereFloorMeters) × Vertical` ≈ **29 cm** above the table.
  - `AppConfig.TerrainReliefExaggeration` (1.2) is an *extra* boost applied to
    terrain relief only, on top of `Vertical`. London's real relief (a few
    tens of metres over 5 km) is close to invisible at true scale, hence the
    small extra boost — but only a small one, because buildings carry the
    visual interest here, not relief.
  - **Both are tuned for the current `RegionSpanKm` and must be rescaled if it
    changes** — `Horizontal` (and everything derived from it) is inversely
    proportional to the span, so shrinking the region 10× without rescaling
    these two multiplies cloud height and terrain relief by that same 10×.
    This shipped wrong once: the region moved from Shanghai (50 km) to London
    (5 km) without retuning them, and a 43.7 m hill rendered as an 84 cm
    spike instead of a gentle ~0.8 cm bump. `tools/verify_logic/Verify.cs`
    ("MapScale (AppConfig defaults)") checks these against the real baked
    `terrain.bin` peak — keep it in sync with `WeatherVRConfig.asset` or it
    stops being a guard.
- Local map space is `[-0.5, 0.5]` on X/Z with Y in VR metres above the map
  plane; `GeoBounds` converts lat/lon ⇄ local.

## Runtime data contract

`Assets/StreamingAssets/WeatherData/`

| File | Format | Notes |
| --- | --- | --- |
| `terrain.bin` | custom binary: magic `PWTR`, ver, w, h, minLat/maxLat/minLon/maxLon, minEle/maxEle (float32), then `w*h` uint16 normalised heights | Little-endian. 512×512 default. |
| `satellite.jpg` | JPEG, 2048² | North-up, exactly covers the same bounds. |
| `weather.json` | see `WeatherDataset` | Grid of cells + layer metadata + provenance. |
| `manifest.json` | JSON | Which files exist, when baked, source attribution. |

If a file is missing the corresponding `*Provider` falls back to procedural
generation seeded from `AppConfig.ProceduralSeed`, so the scene is identical
across runs.

## Performance budget (Pico 6 / Quest 3S class, target 72 FPS)

| Pass | Budget |
| --- | --- |
| Terrain mesh | ≤ 40 k tris after LOD |
| Cloud raymarch | half-res, ≤ 48 steps, early-out at T < 0.01 |
| Lightning | ≤ 3 concurrent bolts, ≤ 512 tris each |
| Everything else | ≤ 2 ms CPU |

`PerfGovernor` measures frame time and drops raymarch steps / resolution
scale before it drops framerate.

## Conventions

- Namespace root: `WeatherVR`.
- No `async void`; use coroutines (Unity 2022, no UniTask dependency).
- Runtime code must not reference `UnityEditor`. Editor-only code lives in
  `Assets/WeatherVR/Editor/`, which Unity compiles into
  `Assembly-CSharp-Editor` by virtue of the folder name. No asmdefs: they
  would have to declare references into the PICO SDK's own assemblies, which
  is a maintenance cost with nothing to buy at this size.
- Every generated asset must be reproducible from a menu item under
  `Tools/WeatherVR/`.

## Known issues found in the handover state

1. **`ENABLE_PICO_XR_SDK` was missing from the Android scripting defines** (it
   was set for Standalone only). Every runtime file in the PICO SDK is wrapped
   in `#if ENABLE_PICO_XR_SDK`, so the APK would have built and installed with
   the entire SDK compiled out and *no compiler error*. Confirmed empirically:
   the `ByteDance.PICO.XR.dll` in `Library/ScriptAssemblies` contains no
   `PXR_HandTracking` type at all. Fixed by `ProjectConfigurator`, which is run
   automatically by `BuildAPK`.
2. **Shaders looked up via `Shader.Find` are stripped from player builds**
   unless referenced by an asset or listed in Always Included Shaders. All
   three WeatherVR shaders are found by name, so `ProjectConfigurator`
   registers them. Without this they return `null` on device only.
3. **TextMeshPro's essential resources are not imported** in this project, so a
   TMP label renders nothing. The provenance HUD uses built-in UI `Text`.
4. **`VerticalExaggeration`/`TerrainReliefExaggeration` were left at their
   Shanghai-era values (4 / 12, tuned for a 50 km span) after the region moved
   to London's 5 km span.** Both scale inversely with `RegionSpanKm` through
   `Horizontal`, so the unchanged multipliers on top of the already-10×-larger
   `Horizontal` rendered the real 43.7 m baked peak as an 84 cm spike and the
   12 km cloud ceiling as a ~19 m shaft — the terrain and buildings looked
   "torn" because buildings were sitting on that spiked surface, not because
   of the OSM building data itself. Fixed by rescaling both 10× (4→0.4,
   12→1.2) in `WeatherVRConfig.asset` and `AppConfig.cs`'s defaults; see
   "Coordinate & scale conventions" above. `tools/verify_logic/Verify.cs`'s
   MapScale block was itself stale (hardcoded the old Shanghai numbers rather
   than reading the live config), so it hadn't caught this — updated to
   the current region/config and it now guards this specific regression.

5. **`SceneBuilder.BuildCard`'s world-space canvas scale didn't divide out
   `mapRoot`'s own scale.** Each provenance card was sized with
   `localScale = 0.3f / canvasW`, which is only correct if the card's ancestor
   chain has no scale of its own -- but the card sits under `mapRoot`, whose
   `localScale = MapSizeMeters` (2.0), so every card actually rendered at
   double its intended 0.3 m width while the stagger offsets between cards
   (correctly expressed in map units, ~0.14-0.28 m apart after the same 2×
   multiply) stayed the same. Three 0.6 m cards spaced ~0.2 m apart pile
   straight on top of each other -- the jumbled overlapping text this
   surfaced as. Same unit-convention bug class as issue #4 above and "The
   unit convention" section below, different call site. Fixed by dividing by
   `config.MapSizeMeters` in the scale line.

## How to (re)build everything

```powershell
pip install -r tools/requirements.txt

# 1. bake data (optional — the app runs fully procedurally without it)
python tools/build_all.py

# 2. in Unity:  Tools ▸ WeatherVR ▸ Build Scene
# 3. in Unity:  Tools ▸ WeatherVR ▸ Build APK   (or File ▸ Build Settings)
```

Batch mode, for a machine with nothing set up:

```powershell
& "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe" `
    -batchmode -quit -projectPath "." `
    -executeMethod WeatherVR.EditorTools.BuildAPK.BuildEverythingFromCommandLine
```

## Demo mode

Real Shanghai weather is clear most days. A genuine snapshot is frequently an
empty sky with zero lightning, which is *correct* and undemonstrable — the
first live bake during development returned CAPE of 3920 J/kg with no rain, so
the lightning proxy correctly produced nothing. Set
`ForceProceduralWeather = true` on the `WeatherVRConfig` asset to always render
the synthetic squall line. The app still labels it "procedural (demo mode)" on
screen, so this shows a storm without claiming one.

## The unit convention (read this before touching anything under the map root)

Local map space is a **normalised unit square**: X and Z run `[-0.5, 0.5]`, Y
uses the *same* units, and the map root's scale (`MapSizeMeters`) turns all
three into VR metres.

Any value computed in VR metres must therefore be divided by `MapSizeMeters`
before being used as a local coordinate. This shipped wrong once — the terrain
mesh built relief in metres while the cloud box and the lightning built theirs
in map units, so terrain was exactly 2× too tall and bolts stopped short of the
ground. Use `MapScale` (via `AppConfig.Scale`) for every conversion; it exists
specifically so this cannot be got wrong silently, and `tools/verify.py` guards
it.

## Progress log

- **2026-07-23** — repo initialised, spec captured, environment audited.
- **2026-07-23** — data layer, terrain + volumetric cloud rendering, lightning,
  procedural audio, interaction, scene orchestration and editor tooling written
  and compile-verified with Roslyn against the Unity and PICO assemblies in both
  define configurations.
- **2026-07-23** — Python bake pipeline working end to end against live sources:
  real SRTM-derived terrain (−89 m to +82 m over the delta, after despiking 6
  pixels of SRTM void speckle), 2048² Esri basemap verified to be Shanghai
  (Huangpu meander, Yangtze estuary, Hongqiao), and a live Open-Meteo snapshot.
- **2026-07-23** — Unity compiled the project clean (`Assembly-CSharp.dll` and
  `Assembly-CSharp-Editor.dll`, no errors, all three shaders imported without
  shader errors). Review pass then found and fixed three bugs that compile fine
  and only manifest on device: the map-unit mismatch above, a missing
  `TrackedPoseDriver` (head tracking would simply not have worked), and
  `SetPixels32` on R8 3D textures. `tools/verify.py` now runs 35 checks against
  the real baked data, all passing.

- **2026-07-23** — scene generated and inspected: all 11 components present,
  every serialized reference wired, map root at the expected scale. Further
  review found and fixed two more silent-failure bugs — a missing
  `TrackedPoseDriver` (now self-installing, so a stale scene cannot lose head
  tracking) and a malformed `file://` URL for StreamingAssets. The shader's
  stereo and depth-sampling macros were checked against Unity 2022.3's actual
  `HLSLSupport.cginc`: `SAMPLE_DEPTH_TEXTURE_PROJ` is correctly redefined for
  the texture-array case, so single-pass-instanced sampling is right.
- **2026-07-24** — region moved to the City of London, buildings pipeline
  added (`tools/fetch_buildings.py` → OSM Overpass, `BuildingDataset`,
  `ProceduralBuildings`, `BuildingMeshBuilder`, `BuildingRenderer`,
  `Buildings.shader`). First render showed spiky terrain and misplaced-looking
  buildings — traced to known issue #4 above (stale exaggeration constants),
  not a data or asset problem. Fixed by rescaling `VerticalExaggeration`/
  `TerrainReliefExaggeration` and updating `tools/verify_logic/Verify.cs` to
  check the live region/config instead of hardcoded Shanghai numbers;
  `tools/verify.py` passes every check against the real baked London
  `terrain.bin` (43.7 m peak renders as 0.84 cm of relief, 2 km cloud sits
  28.8 cm up, atmosphere column 1.89 m tall). Scene has not yet been rebuilt
  or pressed-play with this fix — see "Still to do".

## Still to do

- [ ] **Re-run `Tools ▸ WeatherVR ▸ Build Scene`** and **Press Play** to
      confirm the exaggeration fix (known issue #4) visually — terrain should
      now read as a gentle bump under the buildings, not a spike, and the
      cloud column should sit ~1.9 m above the table rather than towering out
      of frame. Not yet confirmed in the editor, only checked mathematically
      via `tools/verify.py`.
- [ ] **Re-run `Tools ▸ WeatherVR ▸ Build Scene`.** The scene currently on disk
      was generated before the head-tracking fix. The runtime guard makes it work
      anyway, but it will log a warning until the scene is rebuilt.
- [ ] Press Play. This is the first execution of the MonoBehaviour lifecycle,
      the shader variants and the `Texture3D` uploads — none of which the
      out-of-editor checks can reach.
- [ ] Verify on device: frame rate against the 72 FPS target, and that the perf
      governor's tier changes are not visible.

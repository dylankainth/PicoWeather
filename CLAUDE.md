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
      │  ├─ Interaction/  ComfortFollow, XRPointer, HeadTracking, MapPlacementController
      │  ├─ Weather/      WeatherScene (profiles+synth), WeatherSceneDirector, EnvironmentController
      │  ├─ Audio/        AmbientSoundscape, ProceduralAudio
      │  └─ Core/         WeatherSceneController, AppConfig
      ├─ Shaders/         Pedestal.shader, GlassSurround.shader (Resources/), StudioSky.shader,
      │                   Water.shader, TerrainSurface.shader, Buildings.shader,
      │                   WeatherVRGlass.cginc, StudioSkyGradient.cginc
      ├─ Materials/
      └─ Editor/          SceneBuilder.cs, BuildAPK.cs, DataBakeWindow.cs
```

## Coordinate & scale conventions

- **Region:** City of London skyscraper cluster, centre `51.5136°N, -0.0832°E`,
  5 km × 5 km footprint (`AppConfig.RegionSpanKm`). Originally Shanghai at
  50 km × 50 km — see "Deliberate deviations" §2 above for why it shrank.
- **Map size in VR:** 3.0 m across (X/Z). So **1 VR metre = 1.67 km real**
  (`Horizontal = MapSizeMeters / RegionSpanMeters`, i.e. 1:1 667). Was 2.0 m
  until 2026-07-25; see that day's "map at 1.5×" progress entry for the three
  map-unit values that had to be held back or retuned when it grew, because
  everything under the map root is multiplied by this.
- **Vertical exaggeration is two knobs, both scaled to `RegionSpanKm`, not
  absolute constants:**
  - `AppConfig.VerticalExaggeration` (0.4) sets cloud/atmosphere altitude
    scale: `Vertical = Horizontal × VerticalExaggeration`. A 2 km cloud sits
    `(2000 − AtmosphereFloorMeters) × Vertical` ≈ **43 cm** above the table.
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
- **Buildings have their own two knobs**, separate from the terrain/atmosphere
  pair above because buildings are the region's visual subject and are already
  legible at true scale:
  - `AppConfig.BuildingFootprintScale` (1.5) inflates each footprint about its
    own XZ centroid, in `BuildingMeshBuilder`. The City of London is genuinely
    dense, so above 1 neighbouring buildings intersect — that is the accepted
    trade for the city reading as a model rather than a scatter of chips.
  - `AppConfig.BuildingHeightExaggeration` (1.5) multiplies inside
    `MapScale.BuildingHeightToMapUnits`, which still uses `Horizontal` (not
    `Vertical`) as its base. **The ceiling on this is `WeatherVisuals.CloudBaseMeters`,
    not taste:** at 1.5 the bake's tallest tower (310 m) reaches 27.9 cm against
    a 25.2 cm cloud base, so its tip is already ~3 cm inside the deck. That
    overlap is pre-existing and roughly proportional (~1 cm on the old 2 m map at
    true building scale), but it is the number to check before either knob moves.
- **The plinth is deliberately *not* scaled with the map.**
  `AppConfig.PedestalReferenceMapSizeMeters` (2.0) records the map size
  `PedestalMeshBuilder`'s ring radii were authored against, and
  `PedestalMapUnitScale` divides them back down so the plinth holds a constant
  2.28 m × 0.68 m in VR — a table you stand at, not a monument. Consequence,
  accepted: past that reference size the terrain's 0.5-unit edge overhangs the
  crown (36 cm per side at 3 m), so the heightfield's underside is no longer
  covered by `Pedestal.shader`'s `Cull Front` depth pass. Set it equal to
  `MapSizeMeters` to go back to a plinth that grows with the map.
- Local map space is `[-0.5, 0.5]` on X/Z with Y in those *same* normalised units
  (not VR metres — see "The unit convention" below); `GeoBounds` converts
  lat/lon ⇄ local.

## Runtime data contract

`Assets/StreamingAssets/WeatherData/`

| File | Format | Notes |
| --- | --- | --- |
| `terrain.bin` | custom binary: magic `PWTR`, ver, w, h, minLat/maxLat/minLon/maxLon, minEle/maxEle (float32), then `w*h` uint16 normalised heights | Little-endian. 512×512 default. |
| `satellite.jpg` | JPEG, 2048² | North-up, exactly covers the same bounds. |
| `weather.json` | see `WeatherDataset` | Grid of cells + layer metadata + provenance. |
| `flood.bin` | custom binary: magic `PWFL`, ver, w, h, bounds, baseMeters/capMeters (float32), then `w*h` uint16 normalised connection levels | Same grid as `terrain.bin` (must be baked after it). 65535 = "never floods within the baked window". Sidecar `flood.json` carries the bake's provenance/method/limitations in full prose. |
| `forecast.json` | see `ForecastDataset` | Real 5-day hourly forecast + `peakStormDayIndex`, the day judged relatively most storm-prone. |
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

**Correction (2026-07-25): no `PerfGovernor` exists.** This budget table and the
line above describe a runtime adaptive-quality system that was planned but
never built — there is no such file anywhere in the project, and nothing sets
`Application.targetFrameRate` or scales raymarch steps/resolution at runtime.
`AppConfig.CloudMarchStepsMin` and the "perf governor" fields on `AppConfig`
are read by nothing. Treat the table above as a target checked by hand (or by
the profiler on device), not as something the app enforces on its own.

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

If the Android scripting defines are wrong (e.g. a stray `WEATHERVR_AR` left by a phone
AR build), `ProjectConfigurator.Configure()` — called at the top of every build entry
point — corrects them and then deliberately aborts, because removing a define forces a
recompile the same editor invocation cannot see. Run the fix in its own invocation
first, then build in a second, fresh one:

```powershell
& "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Unity.exe" `
    -batchmode -quit -projectPath "." `
    -executeMethod WeatherVR.EditorTools.BuildAPK.PrepareFromCommandLine

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
- **2026-07-25** — head-follow + switchable glass weather scenes (this branch).
  New `Scripts/Weather/` module: `WeatherScene` (five profiles Clear/PartlyCloudy/
  Cloudy/Rain/Storm, each with physical field targets + a glass palette, plus a
  `WeatherDataset` synthesiser — Storm reuses `ProceduralWeather`), `WeatherSceneDirector`
  (replays the existing cloud/rain/lightning `Apply` with the synthesised snapshot
  and sets sun/ambient/fog/surround; terrain+buildings are NOT re-applied, they do
  not change with weather), and `EnvironmentController` (frosted glass floor disc +
  studio-sky re-tint). New `ComfortFollow` (lazy head-follow: eases in front of the
  head, world-upright, secondary button re-centres). Behaviour changes:
    - The map is **no longer placed on a surface / world-locked**. It rides the head
      via `ComfortFollow`; the carousel's `WeatherCarouselFollower` now runs the
      *identical* anchor maths (`ComfortFollow.ComputeAnchor`) off the same camera, so
      terrain and carousel move together. `MapPlacementController` is no longer added
      by `SceneBuilder` (`controller.Placement = null`); the class is kept for
      `DesktopPreview`, which also now disables every `ComfortFollow` so the flat
      orbit preview can look around a map parked at the origin.
    - Tapping a carousel card switches the scene: `WeatherCarouselController` raises
      `SelectionChanged`; `WeatherCarouselFeature` maps the card's `WeatherCarouselIcon`
      to a `WeatherSceneKind` and calls `WeatherSceneDirector.ApplyKind`, grading the
      whole surround toward that card's accent/glass tint so UI and terrain agree.
      Forecast-day cards are kept (each day's condition picks its scene).
    - `StudioSky` defaults brightened (was near-black); `GlassEnvironment.shader`
      added and registered in `ProjectConfigurator`'s always-included list.
  Written to compile against the existing APIs but **not yet compiled in Unity,
  scene not yet rebuilt, not yet pressed-play** — see "Still to do".
- **2026-07-25 (follow-up)** — first test came back "looks exactly the same,
  still blue sky, only rain". Root cause: the environment/director/follow were
  added *only* by SceneBuilder, so on a scene/APK that had not been regenerated
  none of them existed and the old path ran unchanged. Fixed by
  `WeatherSceneBootstrap` (RuntimeInitializeOnLoadMethod, like the carousel):
  it self-installs `ComfortFollow`, `EnvironmentController` and
  `WeatherSceneDirector` at runtime, idempotently, so Press Play works without a
  rebuild. Also: `EnvironmentController.EnsureSky` now assigns the StudioSky
  material at runtime if the scene has none (otherwise the camera falls back to
  Unity's default blue procedural sky); the opening scene defaults to `Clear`
  (bright, terrain visible) instead of the initial data's weather; the carousel
  no longer auto-applies card 0 on load (weather changes on tap only); and
  `DesktopPreview` no longer forces the demo storm. Still uncompiled/untested.
- **2026-07-25 (flood layer)** — added a manual storm-surge overlay: `Shaders/Water.shader`
  (hand-written transparent vert/frag, `ZWrite Off` in the Transparent queue so opaque
  terrain/buildings already in the depth buffer occlude it correctly, single-pass-stereo
  instancing macros added since no existing shader in the repo had them), `Scripts/Flood/FloodRenderer.cs`
  (mirrors `TerrainRenderer`/`BuildingRenderer`: lazy `Shader.Find` material, a single quad
  spanning the map's `[-0.5, 0.5]` square, `SetSurge(index)` over four presets — +0/+2/+5/+10 m
  above the snapshot's `TerrainHeightfield.MinElevation` — converted to map-local Y via
  `AppConfig.TerrainElevationToMapUnits`, the same conversion the terrain mesh itself uses, so
  water and terrain share one vertical frame). Wired into `WeatherSceneController.Build()`'s
  fan-out and a new `SetSurge` entry point, `SceneBuilder.Populate` (WaterSurface under mapRoot),
  `WeatherSceneBootstrap.InstallFlood` (idempotent runtime self-install, same pattern as
  `InstallVisuals`), and `ProjectConfigurator`'s always-included shader list. Control surface is a
  new standalone `Scripts/UI/Flood/FloodPanelBuilder.cs` + `FloodPanelFeature.cs` — four preset
  buttons on their own small world-space panel (reusing `WeatherCarouselInput`'s ray-hit button
  plumbing and `WeatherCarouselFollower`'s wall-mount, stacked below the forecast carousel via a
  `WallHeight` offset), deliberately not folded into the forecast carousel's dataset/scroll
  machinery since flood presets aren't forecast days. Written to compile against the existing
  APIs but **not yet compiled in Unity, scene not yet rebuilt, not yet pressed-play** — see
  "Still to do".
- **2026-07-25 (flood follow-up)** — first live test: controller presses did nothing on the
  flood panel. Root cause: the panel was a *second*, independent `WeatherCarouselFollower` +
  `WeatherCarouselInput` with a hand-guessed `WallHeight` offset — untested placement math with
  no relation to the carousel's own (working) wall-mount, so it likely rendered somewhere the
  controller ray never reached. Replaced entirely: deleted `Scripts/UI/Flood/FloodPanelBuilder.cs`
  and `FloodPanelFeature.cs`; the four surge-preset buttons are now built directly on the
  existing forecast carousel's own canvas (`WeatherCarouselBuilder.CreateFloodRow`, in the
  header's free gap between the location text and the source pill), reusing the exact `xrInput`/
  `canvasRect` the cards and nav arrows already use successfully — no second ray-hit setup, no
  guessed offset. Also implements "flood only in thunderstorms": the buttons are built inactive
  and `WeatherCarouselFeature.OnCardSelected` toggles them active only when the selected card's
  kind is `WeatherSceneKind.Thunderstorm`, and calls `SetSurge(0)` on leaving it so raised water
  doesn't linger into Clear/Rain/etc. Still uncompiled/untested — see "Still to do".
- **2026-07-25 (pedestal + scenery redesign, `.xyz` theme)** — documentation drift
  correction first: the 2026-07-25 entries above describe `WeatherScene` as "five
  profiles" and mention a `GlassEnvironment.shader` being added — neither is what
  actually shipped. `WeatherScene` has **9** profiles (Clear/PartlyCloudy/Cloudy/
  Overcast/Fog/Drizzle/Rain/Thunderstorm/Snow), and no `GlassEnvironment.shader`
  was ever created; the glass surfaces are `Pedestal.shader`, the new
  `GlassSurround.shader`, and `StudioSky.shader`. Also: `PerfGovernor` does not
  exist (see the corrected Performance budget section above) — CLAUDE.md had
  documented one since the project's start with no such file ever existing.
  Real work this round: the hackathon requires one theme from
  {kaleidoscope, .xyz, pawn, reverse} — chose **`.xyz`**, since the app is
  literally x/y/z spatial data (lat/lon, altitude, cloud height), with a
  kaleidoscope facet accent layered on top, toggleable back to plain `.xyz` via
  `AppConfig.PedestalKaleidoscope` (a continuous material float, not a shader
  keyword — every material in this project is created at runtime via
  `Shader.Find` with no material asset backing it, so a `shader_feature` variant
  would have nothing to keep it alive and would be silently stripped on device).
    - **Pedestal** (`PedestalMeshBuilder.cs`, `Pedestal.shader`): went from a
      4-corner square 3-ring plinth to a **chamfered-square, 5-ring, 8-facet**
      profile (32 distinct flat normals across 4 bands). A regular hexagon was
      considered and rejected: the terrain is a square with corners at radius
      0.707, so a hexagon needs a 3.3 m plinth under this 2 m map to cover them
      and would force the carousel's 4-wall mount to become 6-wall — the
      chamfered square keeps the existing footprint and the carousel's wall-snap
      completely untouched. The shader is a two-pass trick on one closed mesh
      (Cull Front opaque interior pass writes depth so the terrain's heightfield
      is never see-through to nothing; Cull Back premultiplied-blend glass pass
      over it) — no `GrabPass`, no framebuffer read anywhere. Coordinate ticks
      use an analytically anti-aliased `AxisLines` (pixel-width lines + a
      distance-based fade, in `WeatherVRGlass.cginc`) so the graticule doesn't
      shimmer at grazing VR angles the way a naive `frac()` grid would.
      Kaleidoscope folds the reflected sky (`KaleidoFold`, matching the sky
      gradient extracted into `StudioSkyGradient.cginc` so pedestal and skybox
      can't drift) through an 8-segment mirror matching the 8 facets, with a
      triple-power fresnel standing in for chromatic dispersion.
    - **Surround**: the glass floor disc that was deleted (see
      `EnvironmentController`'s original header comment) is reintroduced, but
      **opaque**, not blended — `EnvironmentController.EnsureSurround()` builds
      it world-locked, aligned under the map root in XZ. Opaque + `ZWrite On`
      means it writes depth before the skybox draws, so those pixels are
      z-rejected out of the skybox pass entirely: the floor *replaces* fill cost
      rather than adding a blended layer, which was the single biggest
      fill-rate risk identified for this change. `EnvironmentController` is now
      also the one place that publishes the sky palette as shader globals
      (`_WVRSkyZenith` etc.) so every glass surface reflects the same sky
      without re-deriving it.
    - **One palette, not two**: `WeatherSceneProfile` gained `GlassAccent`/
      `GlassTint`, and `SceneKindCarouselDataProvider` (`WeatherCarouselData.cs`)
      now reads them instead of keeping its own independent hardcoded palette —
      the two could previously drift.
    - **Stereo fix**: `Pedestal.shader` and `StudioSky.shader` had no single-pass-
      instanced macros at all. Added, copying `Water.shader`'s (the only shader
      in the repo that had them correctly) — the project's two stereo-mode
      settings files disagree (`PXR_Settings.asset` says multipass,
      `OpenXR Package Settings.asset` says single-pass), so every hand-written
      shader must carry the macros regardless of which one is actually active.
    - **Free perf win taken in passing**: `camera.allowHDR` was never set and
      defaulted true, so the camera was running an FP16 4×MSAA tile buffer for
      no reason — nothing in the project needs HDR (`PostFX.shader` is
      editor-only and not in the always-included list). Set to `false` in
      `SceneBuilder`.
    - **Carousel**: only a modest alpha retint of the existing glass panels/cards
      toward genuine translucency (0.80→0.62, 0.84→0.66) landed this round — the
      diagonal-sheen mesh rewrite and kaleidoscope facet-chip decoration
      discussed during design were descoped to avoid risking the carousel's
      working controller-ray input, which has broken once already in this
      project (see the flood-panel entries above). Left as a follow-up.
  Written to compile against the existing APIs but **not yet compiled in Unity,
  scene not yet rebuilt, not yet pressed-play** — see "Still to do".

- **2026-07-25 (one kaleidoscope, and a 24-hour time slider)** — two changes this
  round, on top of a merge of `master` (18 commits: Cesium, phone AR/touch builds,
  XR simulation settings) into this branch.
    - **The surround was carrying three unrelated symmetries.** `StudioSky.shader`
      folded the azimuth into 12 wedges with its own hand-rolled copy of the fold
      maths; `Pedestal.shader` and `GlassSurround.shader` folded into 8 via
      `KaleidoFold`; and the floor's grid was plain Cartesian with no fold at all.
      So a pedestal facet never reflected a sky that matched it, which is why the
      "these facets are splitting the light" claim in Pedestal.shader's header was
      not actually true on screen. Fixed by moving the whole thing into one place
      (`WeatherVRGlass.cginc`: `KaleidoSegments`/`KaleidoCell`/`KaleidoSplit`/
      `KaleidoDisperse`, replacing `KaleidoFold`) and publishing amount/segments/spin
      as `_WVRKaleido*` globals from `EnvironmentController` — same single-publisher
      pattern, and same reasoning, as the `_WVRSky*` palette globals. `AppConfig`
      gained `KaleidoSegments` (8, matching the plinth's 8 physical facets) and
      `KaleidoSpin`; `PedestalKaleidoscope` now gates the whole surround rather than
      just the plinth, and the sky honours it at last (it previously ignored the
      documented `= 0` plain-`.xyz` fallback entirely).
      Real visual changes on top of the unification: the sky samples its gradient
      along the *folded* direction, so the sun resolves once per wedge — a ring of
      mirrored suns, mirrored *content* rather than the old luminance ripple, and it
      costs zero extra `pow()` because the fold preserves y and the vertical gradient
      never sees it. The floor gained radial mirror spokes on the same wedge layout
      (deliberately **not** spun — a rotating structural line across the lower field
      of view is textbook vection; the moving part stays in the reflection) plus a
      dispersed, seam-lit reflection. The pedestal dropped its per-facet phase offset:
      it randomised each facet's slice, which destroyed the very alignment that makes
      the effect legible now that the surround folds into the same 8 wedges the mesh
      physically has. The sky runs the fold at **0.25× the shared spin rate**, which
      is a comfort decision, not taste: it now carries high-contrast detail across the
      whole periphery, where the rest of the surround is explicitly tuned not to move
      anything fast.
    - **Carousel is days again, plus an hour scrubber.** It had become a flat
      one-card-per-case picker (9 cards). Now: 5 day cards, each carrying a
      `WeatherTimeSegment[]` timeline, and a time slider under the metrics strip
      scrubbing 0–24 h in half-hour steps. Weather is `(day, hour)` resolved through
      `WeatherCarouselItem.KindAtHour`. The authored week covers all 9
      `WeatherSceneKind` cases, because dropping from 9 picker cards to 5 day cards
      must not make any scene unreachable — see
      `DayTimelineCarouselDataProvider`'s header for the two constraints that shaped
      it. `SceneKindCarouselDataProvider` is kept: it is still the quickest way to
      reach one specific case when debugging, and `VisualReviewCapture` uses it.
      The **two-tier update is the load-bearing part**: `WeatherSceneDirector` gained
      `SetTimeOfDay` (sun angle/colour, ambient, fog, sky palette — cheap, runs on
      every frame of a drag) separately from `ApplyKind` (rebuilds the cloud density
      `Texture3D` — runs only when a scrub crosses a timeline boundary into a
      different case). This is the debounce the previous round's "Still to do" asked
      for, arrived at from the other direction. Time of day drives a real 24 h solar
      arc (sunrise 06:00, solar noon 12:00, sunset 18:00, exactly periodic so
      dragging across midnight cannot step), warming the light toward the horizon and
      cooling it to a moonlit floor at night; the profile's `SunAzimuth` is now
      ignored, since with a clock the azimuth has to come from the clock or the sun
      would rise and set in the same place. `WeatherCarouselInput` gained
      `RegisterSlider` — the panel's first *dragged* control rather than a discrete
      hit, on the same canvas/ray plumbing the cards and flood buttons already use
      (deliberately not a second input stack: that is exactly what broke the flood
      panel two rounds ago). The panel canvas grew 440 → 540 downward only, so every
      tuned y position above it still means what it meant and the wall-mount needed
      no retuning.
    - **Two latent bugs found and fixed in `EnvironmentController.Apply` while in
      there**, both products of the `master` merge: the storm-tint block dereferenced
      `_skyMaterial` with no null guard (the guarded block above it had already
      established it can be null), and it published the *raw* `profile.Sky*` colours
      as the `_WVRSky*` globals while setting the *storm-graded* colours on the skybox
      — so every glass surface was reflecting a sky that was not the one overhead,
      which is precisely the drift the single-publisher arrangement exists to
      prevent. Now grades once and publishes what it set.
  Written to compile against the existing APIs but **not yet compiled in Unity, scene
  not rebuilt, not pressed-play** — see "Still to do".

- **2026-07-25 (flood layer: reachability + realism)** — the user reported not being
  able to find the flood simulator at all. It had **not** been removed — `git log`
  shows `FloodRenderer.cs`/`Water.shader` were added, not deleted, in the previous
  commit, and every piece of wiring (`WeatherSceneController.Flood`/`SetSurge`, the
  `WaterSurface` scene object, `WeatherSceneBootstrap.InstallFlood`, the always-
  included shader entry in `GraphicsSettings.asset`, `WeatherCarouselBuilder.
  CreateFloodRow`) was intact. The actual bug: the previous round's day/hour carousel
  rewrite left the flood buttons gated on the *resolved* `KindAtHour`, not the
  selected day, so a Thunderstorm-headline card only exposed them inside its own
  narrow storm window (Monday, 12:00–17:00) — the user's own test landed at 09:30,
  resolved to `Rain`, and correctly showed nothing. On top of restoring reachability,
  two gaps against the app's actual purpose (city flood/storm-surge emergency
  planning) were closed: the water previously snapped to a level in one frame with a
  flat, uniform-alpha quad, which reads as coloured glass, not rising water over real
  ground.
    - **Reachability**: `WeatherCarouselItem` gained `HasKind`, alongside the existing
      `KindAtHour`, scanning the day's `Timeline` for any occurrence of a case rather
      than resolving one at an hour. `WeatherCarouselFeature.UpdateFloodControls` now
      takes the selected `WeatherCarouselItem` and gates on `HasKind(Thunderstorm)`,
      called every `ApplyDayAndHour` (card tap *and* hour scrub) rather than only
      inside the kind-changed guard, since day-level storm status doesn't track hour-
      level kind changes. A cached `floodVisible`/`floodInitialized` pair keeps a
      scrub that doesn't cross a storm-day boundary from re-toggling or re-zeroing the
      surge level every frame.
    - **Animated rise**: `FloodRenderer` now separates a target level (`LevelMeters`,
      unchanged API) from a `DisplayedLevelMeters` eased toward it over `RiseSeconds`
      (2.5s default) with the same smoothstep `EarthIntroFeature.Animate` uses,
      driven from `Update()` on `Time.unscaledDeltaTime` like the rest of the
      project's UI/camera motion. Receding runs the same animation down to the
      terrain's `MinElevation` before disabling the renderer, so leaving a storm day
      reads as water draining away rather than vanishing.
    - **Depth-aware shading**: `FloodRenderer` bakes the terrain heightfield into a
      single-channel `_HeightTex` (same sampling idiom as `ProceduralSatellite.
      Generate`, since `TerrainHeightfield` exposes no raw array) in the mesh's own
      normalised (u,v) — no vertex-shader plumbing needed, `Water.shader` already
      passed `uv` through unchanged. The fragment shader now computes real depth
      (`_LevelMeters` minus the sampled ground elevation), `clip()`s anything above
      water for a shoreline that follows actual valleys and ridges instead of the
      quad's flat rectangular edge, and grades `_ShallowColor` → `_DeepColor` by
      depth with a foam band near the shoreline. `_Color` (the old flat tint) is kept
      declared and still set by `FloodRenderer.WaterColor`, but is no longer sampled
      by the frag — documented in-shader as legacy rather than silently dropped.
    - **Impact readout**: new `FloodImpact` (rendering-agnostic, no GameObject/mesh/
      material) samples each building's footprint centroid against the terrain via
      the same `GeoBounds.ToNormalized` → `TerrainHeightfield.SampleElevation` path
      `BuildingMeshBuilder` uses per vertex, so the readout and the rendered buildings
      always agree on where a building's base sits. `Evaluate` sweeps the full
      heightfield (sub-millisecond even at 512², and only ever called from a button
      press) for submerged-area fraction, plus a building-affected count.
      `WeatherCarouselFeature.RefreshFloodReadout` formats both into a one-line label
      (e.g. `+5m · 23% FLOODED 淹没 · 412/1860 BUILDINGS 建筑`) shown above the
      pagination dots, in the panel's one genuinely free strip of space (dots only
      span x∈[-105,105] of the row).
    - **Labelled controls**: `WeatherCarouselBuilder` adds a `STORM SURGE 风暴潮`
      title in the header-gap's left margin (x∈[-205,-98], left of the four preset
      buttons which occupy x∈[-98,292] of the same gap) — the buttons previously had
      no label identifying what they were at all.
  Written to compile against the existing APIs but **not yet compiled in Unity, scene
  not rebuilt, not pressed-play** — see "Still to do".

- **2026-07-25 (real hydraulic connectivity + real forecast + storm-day selection)** —
  the flood layer's shoreline was real terrain but a fake flood: it was still a pure
  bathtub, and the storm day was still authored fiction (`DayTimelineCarouselDataProvider`'s
  hand-written week). Both replaced with real data this round, plus a live check of
  whether London is actually due a storm (it is not — see below).
    - **New bake step, `tools/fetch_flood.py`**: computes, per terrain cell, the
      *minimum water level at which that cell is hydraulically connected to the
      river* — a priority-flood (Dijkstra with `max` instead of `+`, i.e. a minimax/
      bottleneck shortest path) seeded from real Thames geometry (OpenStreetMap
      `waterway=river`/`riverbank`, fetched the same way `fetch_buildings.py` already
      hits Overpass) through a barrier grid raised to real EA flood-defence crest
      heights wherever one exists (`SpatialFloodDefencesIncStandardisedAttributes`,
      the EA's ArcGIS FeatureServer — confirmed live and key-less this round;
      `MapServer` on the same host 500s, `FeatureServer` works). Output is
      `flood.bin` (`PWFL`), same grid as `terrain.bin`, read by the new
      `FloodConnectivityField` (mirrors `TerrainHeightfield`'s binary layout and
      bilinear sampler exactly). A cell only floods once the water level reaches its
      connection level, not merely once the level exceeds its ground elevation — the
      old bathtub's actual error. First live bake found 370 defence features
      intersecting the tile, crest heights 5.17–8.75 m AOD; seeding from *all* mapped
      water (rivers, canals, docks) barely changed anything (0.1–0.6pp vs bathtub)
      because impounded water — St Katharine Docks, canal basins — sits inland of the
      tidal defences and a flood seeded there starts on the dry side of the wall;
      restricting seeds to `waterway=river`/`riverbank` only fixed this (see
      `fetch_flood.py::classify_way`), producing a real, defensible result: defences
      hold back 19 percentage points of the tile at 3.5 m AOD (a high tide within
      their crest range) but the advantage narrows toward nothing above ~4.5 m,
      because EA's own asset coverage for this specific 5 km tile is 370 discrete
      records, not a continuous wall along every metre of bank, and 90 m-class SRTM
      terrain cannot resolve an engineered embankment where no EA record exists —
      documented as a real, stated limitation in the bake's own provenance
      (`flood.json`) rather than smoothed over. Presets in the UI stayed
      relative-to-terrain-minimum (`+0/+2/+5/+10 m`) rather than switching to
      absolute AOD, since that is what the existing carousel chips and
      `FloodRenderer.SurgePresetsMeters` already mean; `fetch_flood.py`'s own CLI
      summary reports both relative presets during the bake and absolute AOD levels
      (`REPORT_LEVELS_AOD`, chosen against this tile's real numbers) so the two
      framings can be cross-checked by eye.
    - **Runtime plumbing**: `FloodRenderer` gained `EnsureConnectivityTexture`, a
      second baked lookup texture (same resolution/uv convention as the existing
      terrain-height texture) alongside three new shader uniforms (`_ConnectTex`,
      `_ConnectMin`, `_ConnectRange`); `Water.shader`'s fragment now clips on
      `min(depth, connected)` instead of `depth` alone — connectivity is provably
      always the tighter gate (a connect level is, by construction, `max(terrain,
      defence-crest)` along some path, so it can never be below the cell's own
      ground), kept as a second explicit `clip()` term rather than folded together
      for defence-in-depth against any float/format mismatch between the two
      independently-baked textures. `FloodImpact`'s submerged-area sweep and
      per-building check now read `FloodConnectivityField.ConnectLevelAt`/
      `SampleConnectLevel` instead of raw terrain elevation, so the readout and the
      rendered water can never disagree. `WeatherSnapshot` gained `Flood`
      (`FloodConnectivityField`) alongside `Terrain`/`Weather`/`Buildings`, loaded by
      a new `WeatherDataService.LoadFlood` following the exact same
      baked-then-procedural pattern as every other layer — the procedural fallback
      (`FloodConnectivityField.Procedural`) is the old bathtub verbatim (connect
      level = terrain elevation), so a network-down demo degrades to exactly the
      prior behaviour rather than to nothing.
    - **New bake step, `tools/fetch_forecast.py`**: a real 5-day hourly forecast from
      Open-Meteo (same key-less endpoint `fetch_weather.py` already uses, requested
      with `timezone=Europe/London` so calendar days line up with local wall-clock
      hours) — WMO weathercode mapped to `WeatherSceneKind`
      (`WEATHERCODE_TO_KIND`), collapsed to the same start-hour/kind segment shape
      the carousel's timeline already uses. Each day also gets a **storm-likelihood
      score** (`storm_score`: literal thunderstorm hours dominate outright, then CAPE
      and gust speed, precipitation weighted lightly on its own) purely to *rank the
      five real days against each other* — `peakStormDayIndex` names the day the
      flood simulator's storm-surge controls key off. This is the direct answer to
      "point the flood demo at whichever real day is most likely to storm": the
      first live bake (25–29 Jul 2026) found **zero literal thunderstorm hours in
      the entire window** — Tuesday 28 Jul (CAPE 360 J/kg, otherwise unremarkable)
      ranked highest and is the designated day, correctly labelled by its own real
      condition (`PartlyCloudy`), not forced to say "storm" it isn't having.
    - **Runtime plumbing**: new `ForecastDataset`/`ForecastDay` (`Data/ForecastDataset.cs`,
      flat parallel arrays for the timeline, same reason `BuildingRecord` flattens its
      footprint — `JsonUtility` cannot deserialise a jagged array). `WeatherSnapshot`
      gained `Forecast`, loaded by `WeatherDataService.LoadForecast` — baked-only, no
      live-fetch path (a 5-day hourly request is heavier than the tiny live snapshot
      `LoadWeather` already makes at startup elsewhere in this project), falling back
      to `null` so the carousel's own fallback below is what actually degrades
      gracefully. New `ForecastCarouselDataProvider` builds real day cards from it —
      same shape as `DayTimelineCarouselDataProvider` (headline/icon/accent/tint all
      still come from the shared `SceneKindCarouselDataProvider.Describe`/
      `WeatherScene.Default` tables, only the numbers and timeline are real) — and
      `WeatherCarouselItem` gained `IsPeakStormDay`, since a real forecast usually
      has no literal Thunderstorm segment at all and `HasKind` alone would leave the
      storm-surge controls permanently unreachable; `WeatherCarouselFeature`'s gate is
      now `HasKind(Thunderstorm) || IsPeakStormDay`. `WeatherCarouselDataProvider.
      CreateDefault()` now returns a new `FallbackCarouselDataProvider` wrapping
      `ForecastCarouselDataProvider` then `DayTimelineCarouselDataProvider` — real
      data first, procedural fallback always succeeds, the same policy
      `WeatherDataService` already applies to every other layer, applied here to the
      carousel's own data source for the first time.
    - **Verified, not merely written this time**: every bake step above was actually
      run against the live APIs (`python tools/fetch_flood.py`, `fetch_forecast.py`,
      and both through `build_all.py`) and produced the real numbers quoted above,
      not just designed. All nine touched/added C# files were also compile-checked
      for real — Unity's own bundled Roslyn (`Data/DotNetSdkRoslyn/csc.dll`, run
      through its bundled `NetCoreRuntime/dotnet.exe` host, referencing the actual
      installed Editor's `Managed/UnityEngine/*.dll` plus this project's own
      `Library/ScriptAssemblies/*.dll`) compiled the full runtime script set clean —
      zero errors, only pre-existing warnings unrelated to this change — in both the
      default defines and with `ENABLE_PICO_XR_SDK` added, since the Unity Editor
      itself was open in another session and could not be closed to run a batch-mode
      compile. This is a real step up from every prior round's "written to compile,
      not yet compiled" caveat, but it is still not the same as Unity's own
      compile — it does not catch anything the Editor's own AssetDatabase/importer
      pipeline would (a missing `.meta`, a shader compiled by ShaderLab rather than
      plain Roslyn — `Water.shader`'s new properties/clip were checked by eye only).
      Scene not rebuilt, not pressed-play — see "Still to do".

- **2026-07-25 (real hardware: no ray, no controls at all)** — first test on an actual
  PICO headset (not the emulator, not the editor): fully immersive, head tracking
  worked, but no pointer ray was ever drawn and no card/button press did anything.
  Investigation found four separate faults, only one of which explains the reported
  symptom — the other three are real but were masked or hadn't bitten yet.
    - **The pointer ray has never rendered, on any platform, ever.**
      `SceneBuilder.ConfigureRayVisual` configures the `LineRenderer` (material,
      gradient, width) but nothing anywhere ever called `SetPosition` on it, so the
      baked scene kept Unity's brand-new-component default of `(0,0,0) -> (0,0,1)` in
      world space — a static line lying on the floor through the origin, not a ray
      from the hand. `XRPointer` never held a `LineRenderer` reference at all; it only
      wrote `PoseSource.SetPositionAndRotation`, which moves the anchor transform, not
      a world-space line's baked points. This alone explains "no ray at all", and made
      aiming blind, which explains "no button did anything" as a direct consequence —
      not a separate bug. Fixed with new `Scripts/Interaction/XRPointerVisual.cs`
      (`[DefaultExecutionOrder(200)]`, idempotent `Ensure(GameObject, XRPointer)`
      called from both `SceneBuilder.Populate` and a new
      `WeatherSceneBootstrap.EnsurePointerVisual`, same bake-plus-runtime-repair
      pattern `HeadTracking.Ensure` already established): hides the line entirely
      when untracked rather than collapsing it to zero length (a degenerate line with
      capped vertices still submits a draw call — the same failure in miniature), and
      terminates the ray at `WeatherCarouselInput`'s own canvas-hit distance (new
      `RayHitDistance` property, set in `ProcessRay` only when the hit lands inside
      the panel's rect, reset every frame including on the screen-pointer/gaze
      branches) rather than a fixed length that would visibly punch through the
      glass. Also draws a small view-facing reticle (a second `LineRenderer` using
      `LineAlignment.View` and the ray's own already-always-included `Sprites/Default`
      material, no new shader) that brightens on-target, and — since
      `WeatherCarouselInput`'s 1.25 s gaze-dwell fallback had zero visual feedback of
      its own, indistinguishable from a dead app — shows that same reticle swelling
      with dwell progress (new `GazeProgress01`/`GazeWorldPoint` on
      `WeatherCarouselInput`) whenever the controller isn't tracked at all.
    - **`XRPointer` only ever tried one detection strategy for a controller,** and a
      real PICO controller reporting characteristics slightly differently than
      `HeldInHand|Controller|Right/Left` (or its existing no-handedness fallback)
      would have silently gone untracked forever with no diagnostic anywhere. Widened
      into six ordered tiers (`XRPointer.PoseTier`, logged once on change): the
      original two, then a legacy any-non-headset-device search, a generic
      `UnityEngine.InputSystem.XR.XRController` walk (independent of the legacy
      `InputDevices` bridge — `activeInputHandler` is already `Both`, so this second
      bridge is live for free; compiled only under `UNITY_INPUT_SYSTEM_ENABLE_XR` so
      its absence is a no-op tier, not a compile error), PICO's own native
      `PXR_Input.GetControllerPredict{Position,Rotation}` pose (latched unavailable on
      `DllNotFoundException`, mirroring the existing hand-tracking latch, since it is
      the same arm64-only native library), and the original static-anchor fallback
      last. Also fixed a real discard bug in the process: the legacy device read
      computed `selecting`/`secondary` from buttons *before* checking `isTracked`,
      except the earliest `isTracked`-false return happened before any button was
      read at all — so a momentary tracking dropout silently ate a genuine trigger or
      secondary-button press. Reordered so buttons are always read first; only the
      returned pose validity is gated on tracking. Which of these tiers is actually
      load-bearing on real PICO hardware is unknown — that is what the diagnostics
      below are for.
    - **No Android XR loader is assigned in the committed project.**
      `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` had Android
      `m_InitManagerOnStart: 0` and `m_Loaders: []` — git-clean, i.e. this is the
      state anyone else checks out. Traced to `BuildPhoneTouch.DisableXr`'s output
      (correct for the phone-touch build) having been committed onto the PICO
      mainline in an earlier round, with nothing putting the PICO loader back
      afterwards. This is *not* what caused this test's symptom (the app ran fully
      immersive, so whatever local build was actually installed had a loader) but it
      is a live landmine: the PICO SDK's own manifest post-process step
      (`Packages/com.bytedance.pico.xr/Editor/PXR_BuildProcessor.cs`) skips writing
      `pvr.app.type=vr` when it finds no loader assigned, so the *next* clean build
      would launch as a flat 2D panel with no error anywhere. Fixed on three levels:
      (1) the committed asset restored to the last known-good state (PXR_Loader
      assigned, `InitManagerOnStart: 1`); (2) new
      `ProjectConfigurator.EnsureAndroidXrLoader`, called from `Configure()` (which
      every build entry point calls) — purges any non-PICO loader, assigns PICO, and
      falls back to adding the loader asset directly by GUID if
      `XRPackageMetadataStore.AssignLoader` silently no-ops against a cold metadata
      cache (a real failure mode in batch mode, not hypothetical: the cache is
      populated by the interactive XR Plug-in Management window, which a batch run
      never opens); both phone build variants call `Configure()` and then immediately
      swap the loader to their own choice, so this cannot break them; (3) new
      `PicoBuildGuard` (`IPreprocessBuildWithReport`, order 2000, after XR
      Management's own preprocessor has already decided what to bake into the
      player) throws a `BuildFailedException` if the loader is still missing at that
      point regardless — belt and braces, since by then it is too late to fix,
      only to refuse. `BuildPhoneAR.SwitchToPico` (menu item) previously
      reimplemented this same swap by hand and forgot to re-enable
      `InitManagerOnStart`; it now just calls the one shared implementation. Also
      found and removed a second leftover: `WEATHERVR_AR` was still in the Android
      scripting defines (from a prior phone-AR build). `EnsureAndroidDefines` now
      strips forbidden symbols as well as adding required ones, and `Configure()`
      deliberately throws when it does — removing a define forces a recompile the
      same editor invocation cannot see (the same hazard
      `BuildPhoneAR.EnableArDefineFromCommandLine` already documented for adding one)
      — so a new `BuildAPK.PrepareFromCommandLine` batch entry point exists to run
      that correction in its own invocation before the real build.
    - **`XrBootDiagnostics`** (new, `Scripts/Core/`) logs one block tagged `XRBOOT`
      8 frames after scene load: active loader, `XRSettings.isDeviceActive`,
      tracking-origin mode, an on-screen banner plus `Debug.LogError` if no loader
      ever activated on a mobile build, and — the part this investigation actually
      needed and didn't have — the complete unfiltered `InputDevices.GetDevices()`
      dump (name, full characteristics mask, tracked, has-position, has-rotation) for
      every device the runtime reports. This is what should decide which of the
      pose-detection tiers above are load-bearing on the actual hardware, rather than
      guessing from documentation.
    - **`PXR_ProjectSetting.stageMode` was `0`** (eye-level-relative poses) while
      `SceneBuilder.Populate`'s `CameraOffset` math has a comment asserting the
      opposite and zeros the offset on that basis. The map's own placement is masked
      by `ComfortFollow` being head-relative regardless, but
      `EnvironmentController`'s glass floor is not — it sits at world Y=0 under the
      assumption that means "the floor", which with an eye-level origin means
      "through the user's head". Fixed both halves, since neither alone is
      sufficient: `stageMode` set to `1`, *and* `HeadTracking.Ensure` (now taking an
      optional `cameraOffset` parameter) probes the actual `XRInputSubsystem` tracking
      origin mode at runtime, tries to force `Floor`, and if it still reports
      `Device`, lifts the camera offset by a 1.6 m standing eye height instead — so
      world Y keeps meaning "metres above the floor" either way, rather than trusting
      a comment to stay in sync with a project setting a second time.
    - **Head-follow, requested this round, restored via the existing machinery.**
      `ComfortFollow` was never deleted (`309b9c2` only stopped attaching it to the
      map root and had `WeatherSceneBootstrap.WorldLockMap` actively destroy any
      found on it) — CLAUDE.md's own prior entries describing head-follow as shipped
      were stale. Re-attached in `SceneBuilder.Populate`, and
      `WeatherSceneBootstrap.WorldLockMap` replaced with its exact inverse,
      `EnsureMapFollow` (idempotent: adds one if missing, backfills `Head`/`Pointer`
      if unset, warns to rebuild the scene). Discovered in the process that the
      carousel already mounts in **map-local** space
      (`WeatherCarouselFeature`'s `follower.Anchor = sceneController.MapRoot`), so
      making the map follow the head makes the carousel follow for free — zero
      changes needed to `WeatherCarouselFollower` itself. Two things this round got
      right that `309b9c2`'s original values would have gotten wrong: (1) the
      carousel panel sits `WallHalfExtent(0.68) × MapSizeMeters(2.0) = 1.36 m` toward
      the user from the map centre, so restoring the deleted component's original
      `Distance = 0.95f` would put the panel *behind* the user's head — used `2.45f` /
      `-0.55f` instead, the values `WeatherSceneBootstrap`'s one-shot placement had
      already been using and which are the framing actually tested; (2) a continuous
      head-relative follow at that 2.45 m lever arm would slide the entire world
      sideways by roughly a metre for every 25° glance, which is not "the table comes
      with me", it's vection — so `ComfortFollow` gained `YawDeadzoneDegrees`/
      `PositionDeadzone` fields (both defaulting to `0`, bit-identical to the old
      always-recompute behaviour for every other consumer, including the carousel's
      own head-relative fallback branch) and the map's instance sets a 25° yaw
      deadzone: it holds its anchor until the user turns meaningfully, then re-targets
      and eases there, rather than tracking every frame. `DesktopPreview` changed from
      disabling `ComfortFollow` to destroying it outright, and reordered *before* the
      flat-preview's own position reset — `Destroy()` only takes effect at the end of
      the frame, so leaving the reset first would have let one more `LateUpdate` run
      and silently overwrite it.
  All of the above is written to compile against the existing APIs — the file-level
  reasoning (namespace-qualifying every new Input System / XR Management type instead
  of adding `using` directives, since `UnityEngine.XR.CommonUsages` and
  `UnityEngine.InputSystem.CommonUsages` are two different types with the same short
  name and the file already has the former in scope unqualified) was checked by hand
  against the installed package sources in `Library/PackageCache`, not run through
  Unity's own compiler — no Unity Editor session was available this round. **Not yet
  compiled, scene not rebuilt, not pressed-play, and none of it has touched real
  hardware yet** — see "Still to do".

- **2026-07-25 (map at 1.5×, branch `bigger-map-1.5x`)** — `MapSizeMeters` 2.0 → 3.0,
  so the scale went 1:2 500 → 1:1 667 and everything authored in the normalised map
  square grew with it for free. The work was almost entirely in deciding what must
  *not* be multiplied by that 1.5, since map units are the project's standing
  silent-failure mode (see the unit-convention section and known issue #4 — same class
  of bug, third occurrence).
    - **Plinth held at a fixed VR size.** `PedestalMeshBuilder.Build` gained a
      `unitScale` parameter (default 1, so nothing else changes) applied uniformly to
      every ring radius, chamfer *and* height — uniform on purpose: scaling the three
      together is what preserves all 32 facet slopes the shader's flat normals depend
      on, where scaling radii alone would have squashed them. `PedestalRenderer` passes
      the new `AppConfig.PedestalMapUnitScale`
      (`PedestalReferenceMapSizeMeters / MapSizeMeters` = 2/3), so the plinth still
      measures 2.28 m × 0.68 m. The authored radii were left alone rather than
      re-tuned, so one scalar records the departure instead of the numbers drifting.
      **Accepted consequence:** the crown pulls in to 0.380 map units while the terrain
      still reaches 0.500, i.e. the map overhangs its plinth by 36 cm per side and the
      heightfield's underside is no longer hidden by `Pedestal.shader`'s `Cull Front`
      depth pass. This was raised before the change and chosen deliberately; it is
      asserted in `Verify.cs` rather than left to be discovered on device.
    - **Buildings given an extra 1.5× on top**, via two new `AppConfig` knobs:
      `BuildingFootprintScale` (inflation about each footprint's own XZ centroid, in
      `BuildingMeshBuilder` — needs a pre-pass for the centroid, and is winding-safe
      because a uniform positive scale about an interior point preserves orientation)
      and `BuildingHeightExaggeration` (a new optional 6th `MapScale` ctor argument,
      defaulting to 1 so `Verify.cs`'s five-argument construction still compiles and
      still means true scale). Terrain elevation is still sampled at the *true*
      geographic point, not the inflated one, so a base sits on the ground actually
      under the building. Net: 2.25× the previous VR size. Two real costs, both
      documented at the knobs rather than smoothed over — inflated footprints
      **intersect** in the genuinely dense City core, and the bake's tallest tower
      (310 m) now reaches 27.9 cm against a 25.2 cm cloud base, so its tip sits ~3 cm
      inside the deck (pre-existing and roughly proportional: ~1 cm on the 2 m map at
      true scale, but it is now the binding constraint on both knobs).
    - **Cloud gap 1.5× again on top of the map's own 1.5×.**
      `WeatherVisuals.CloudBaseMeters` 900 → 1250 m, deck thickness unchanged, so the
      clear-air gap goes 11.2 cm → 25.2 cm (2.25× total) and the deck top at 1950 m
      still sits well inside `AtmosphereCeilingMeters`.
    - **Two values retuned so the carousel did not move.** Both are the map-unit trap
      again: `WeatherCarouselFollower.WallHalfExtent` 0.68 → 0.62 (the terrain edge at
      0.5 is now the outermost thing to clear, since the plinth pulled in — 0.62 keeps
      the same 36 cm of real clearance proud of the map edge), and the map's
      `ComfortFollow.Distance` 2.45 → 2.95 in both `SceneBuilder.Populate` and
      `WeatherSceneBootstrap.EnsureMapFollow`. Left alone, the panel would have ridden
      1.86 m out from a map centre 2.45 m away, i.e. 0.41 m from the user's face and
      behind the map's own overhang. At 2.95 the tested framing is preserved exactly:
      panel 1.09 m from the head, near table edge 1.45 m.
    - **Verified, not merely written:** `python tools/verify.py` compiled all 54
      runtime scripts from source and passed every check, including a new
      "Map at 1.5×: what had to be held back" block that asserts the plinth's VR
      dimensions are unchanged, that the overhang is the expected 36 cm, that the
      carousel keeps both its edge clearance and its head distance, that the building
      exaggeration is exactly the configured factor, and that the cloud gap is 2.25×
      what it was. The `MapScale` block's own constants were updated to the live
      config, per that block's own standing warning. Editor-only code
      (`SceneBuilder.cs`) is outside what `verify.py` compiles — that edit is one float
      plus comments. Scene not rebuilt, not pressed-play, not on hardware — see
      "Still to do".

- **2026-07-25 (merged into `master`: t5115's locomotion + particle work, and the
  1.5× map)** — `origin/master` had moved on three commits: Dylan's
  `Water.shader` ShaderLab fix plus an APK rebuild, and two from Tahmid Ahmed
  (`tahmid.ahmed5115@gmail.com`) adding PICO joystick locomotion and a
  precipitation rewrite. `feature/london-scene-scale-fixes` was already an ancestor
  of `origin/master`, so nothing was outstanding there. `spatial-kotlin` was
  deliberately **not** merged: it is a parallel Kotlin/PICO-Spatial rewrite of the
  data layer (~5 300 lines under `spatial/`), a different track from this Unity app,
  not t5115's work, and merging it would put a second implementation of the same
  domain in the tree.
    - The merge of the 1.5×-map branch was textually clean — the two sides touched
      disjoint regions of `WeatherVisuals.cs`, `SceneBuilder.cs` and
      `WeatherSceneBootstrap.cs`. `tools/verify.py` passes and compiles all 55
      runtime scripts (up from 54: `ControllerLocomotion.cs`).
    - **New code reviewed rather than assumed.** `ControllerLocomotion` reads both
      thumbsticks through `CommonUsages.primary2DAxis`, with the same
      characteristics-then-any-device fallback ladder `XRPointer` already uses, and
      collapses to movement-only when one device answers for both hands. Its
      interaction with the head-following map is the load-bearing part and is
      correct: `ComfortFollow.PreserveWorldPoseAfterRigMove` resets only
      `_anchorYaw`/`_anchorPosition` — the *deadzone reference*, not `_targetPosition`
      — so artificial locomotion moves the user around a map that keeps its world
      pose, instead of dragging the map along. Called every frame the stick is held,
      which is what stops a stick-driven yaw from crossing the 25° deadzone and
      re-targeting.
    - **One stale-copy bug found and fixed in the incoming test.**
      `VisualComfortTests.PrecipitationStylesAreLargeAndCoverEverySliderWeatherCase`
      hardcoded `0.056f` for the cloud base in map units — right only for a 900 m base
      on a 2 m map, both of which had just moved. It could not fail loudly, because
      `cloudBaseMap` feeds only fall speed and the test asserts sizes and emission, so
      it would have passed forever while describing a configuration the app no longer
      had. Now derived from `AppConfig.AltitudeToMapUnits(WeatherVisuals.CloudBaseMeters)`,
      which required making those two constants public. Third instance of this exact
      class in the project, after the Shanghai-era exaggerations and the `Verify.cs`
      MapScale block.
    - **Two interactions between the two sides worth watching on device**, neither a
      defect: (1) Tahmid's precipitation sizes were deliberately scaled up several
      times for legibility at headset resolution and are expressed in map units, so
      the 1.5× map multiplies them again — snow flakes now render at roughly 6–9 cm.
      (2) `ControllerLocomotion.MinimumZoomDistance` (0.55 m) is measured from the
      exhibit *centre*, which is now well inside the map's 1.5 m half-extent, so
      zooming fully in puts the user over the middle of the city rather than at its
      edge. Both were already true in proportion on the 2 m map; both are now more
      pronounced, and both are single-field changes if they read badly.

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
- [ ] **Head-follow + glass scenes (2026-07-25):** rebuild the scene, then in the
      PICO OS6 Emulator confirm: (1) terrain + carousel stay in front and move
      together as you turn/walk, secondary button re-centres; (2) tapping each of
      the five cards visibly changes clouds/rain/lightning/sky and the glass tint;
      (3) the surround is the lit glass environment (floor disc + graded sky), not
      black. None of this is confirmed yet — code is written but uncompiled.
- [ ] Re-check the cloud/rain/lightning `Apply` cost on a card tap (it rebuilds the
      density `Texture3D`). Fine as an occasional switch; if a rapid card-swipe
      stutters, debounce `ApplyKind` behind the carousel's settle.
- [ ] **Flood layer, reachability + realism fix (2026-07-25):** the shader is already
      registered in `GraphicsSettings.asset` (confirmed by reading the asset directly),
      so only `Tools ▸ WeatherVR ▸ Build Scene` (not required — WeatherSceneBootstrap
      self-installs `WaterSurface` idempotently) and Press Play remain. Confirm: (1) no
      compiler/shader errors; (2) selecting the Thunderstorm day (Monday, day index 2)
      shows the `STORM SURGE 风暴潮` label and all four preset buttons **immediately**,
      at any hour of that day — not only inside its 12:00–17:00 Thunderstorm window,
      which is the bug this round fixed (buttons were gated on the resolved hour's
      kind, so the user's own test landed at 09:30/Rain and saw nothing); (3) pressing
      a preset raises water smoothly over ~2.5 s instead of popping in; (4) the
      waterline follows real terrain — valleys flood, ridges stay dry, a foam band
      sits at the true shoreline, shallow water reads pale, deep water dark — instead
      of a flat translucent rectangle; (5) the readout below the pagination dots
      tracks each press (e.g. `+5m · 23% FLOODED 淹没 · 412/1860 BUILDINGS 建筑`) and
      both numbers rise monotonically with the level; (6) scrubbing the clock across
      12:00/17:00 within the storm day changes the weather but leaves the buttons
      visible and any raised water in place; (7) selecting a different day hides the
      row, animates the water back down, and returns it to +0m; (8) on device, water
      renders to **both eyes** (the stereo macros were unverified until now) and frame
      time holds near 72 FPS with `+10m` raised during Thunderstorm — the densest
      frame the app can produce.
- [ ] **Pedestal + scenery redesign (2026-07-25):** run
      `Tools ▸ WeatherVR ▸ Add Shaders to Always-Included`, then
      `Tools ▸ WeatherVR ▸ Build Scene`, then Press Play. Confirm: (1) no
      compiler/shader errors; (2) the plinth reads as a faceted glass shell over a
      darker interior with coordinate ticks visible *through* the glass, and the
      terrain's corners are fully covered by it (the specific geometry regression
      the chamfer-vs-terrain-radius check in `PedestalMeshBuilder.cs` guards
      against) — **superseded 2026-07-25 by the map-at-1.5× change: the corners are
      now deliberately *not* covered, since the plinth is held at a fixed VR size
      while the map grew past it. Check the overhang looks intentional instead**;
      (3) a floor grid is visible under the table, fading toward its
      edge, with no hard disc boundary; (4) tapping through all 9 carousel cards
      shifts sky, fog, floor tint and pedestal rim together (the palette-unification
      payoff — the fastest way to spot a value that was left hardcoded); (5) the
      carousel still mounts correctly and the controller ray still hits every
      card and the flood buttons (the wall-mount math itself was not changed,
      but confirm it — carousel input has broken silently once before); (6) with
      `AppConfig.PedestalKaleidoscope` set to 0 on `WeatherVRConfig.asset`, the
      pedestal and floor still render correctly as plain `.xyz` glass with no
      facet fold and no error/pink shaders — this fallback path is designed but
      not yet proven. On device specifically: floor/pedestal/sky render to both
      eyes (new stereo macros, ambiguous stereo-mode setting), and check frame
      time looking across the floor at a grazing angle (worst case for both fill
      rate and grid aliasing) and during a Thunderstorm with a flood surge raised
      (densest frame the app can produce).
- [ ] **Unified kaleidoscope (2026-07-25):** compile, then Press Play. Confirm:
      (1) no shader errors — `WeatherVRGlass.cginc` is now included by four shaders
      and the `_WVRKaleido*` globals are declared in three of them; (2) the sky shows
      a ring of mirrored suns near the horizon rather than one sun, and the wedge
      seams in the sky line up with the plinth's facets and the floor's spokes (that
      alignment is the entire point of the change — if the counts look different,
      something is not reading the shared global); (3) with
      `AppConfig.PedestalKaleidoscope = 0` the sky is a plain gradient with a single
      sun, the floor has no spokes, and the plinth has one unfolded reflection —
      this fallback never worked for the sky before, so it is genuinely untested;
      (4) on device, the sky's 0.25× spin is slow enough to be comfortable in
      peripheral vision — the one judgement call here that a still frame cannot
      settle.
- [ ] **Day/hour carousel (2026-07-25):** compile, then Press Play. Confirm:
      (1) five day cards (TODAY/TOMORROW/weekday) instead of nine case cards, and the
      panel is taller with a time row below the metrics strip; (2) the controller ray
      can *drag* the knob — this is the panel's first dragged control and the input
      path most likely to be wrong; (3) the clock and condition labels track the drag,
      and the knob does not read 00:00 while pinned to the far right; (4) scrubbing
      within one timeline segment changes only the light, while crossing a boundary
      visibly switches the weather — if a scrub stutters continuously, the
      cheap/expensive split in `WeatherSceneDirector` is not holding; (5) 03:00 is
      night with a dim blue sky and a moonlit floor, 06:00/18:00 warm and low,
      12:00 full daylight, and the sun visibly tracks east→west across the drag;
      (6) selecting the storm day shows the flood buttons at every hour of that day
      (not only inside its 12:00–17:00 Thunderstorm window — see the flood-layer
      entry below, which corrects this item), and selecting a different day hides
      them and drops the water; (7) all 9 cases are reachable across the five days
      (Fog/Clear/PartlyCloudy day 1, Cloudy/Overcast/Drizzle/Rain day 2,
      Thunderstorm day 3, Snow day 5).
- [ ] **Real hydraulic connectivity + real forecast (2026-07-25):** re-run
      `python tools/build_all.py` (or at minimum `fetch_terrain.py` then `fetch_flood.py`
      then `fetch_forecast.py`) to refresh the baked payload, then in Unity: no scene
      rebuild is required (`WaterSurface`/the carousel are both runtime self-installing),
      but this has not been through Play mode at all this round — everything below is
      unconfirmed beyond the standalone Roslyn compile-check. Confirm: (1) no console
      errors on load, especially `[WeatherVR] flood.bin unusable` / `forecast.json
      unusable` (either would mean a silent fall back to the old bathtub / authored week
      rather than a crash, but should not fire against a fresh bake); (2) the carousel
      shows five real day cards with real temperatures/wind/humidity/rain-chance
      (compare against the numbers `fetch_forecast.py` printed at bake time) instead of
      the authored TODAY/TOMORROW/... week; (3) the storm-surge controls appear on
      whichever day the bake printed as "highest storm likelihood" (Tuesday 28 Jul in
      the first live bake — re-check, since a forecast this many days out will have
      changed by the time this is tested) at every hour of that day, even though its
      condition may read as something ordinary like "Partly Cloudy" rather than a
      literal storm — that mismatch is intentional (see the progress-log entry) but is
      worth a second look in case it reads as confusing rather than honest in practice;
      (4) the shoreline now visibly respects real defences — at low surge presets water
      should stay behind the Thames frontage rather than filling every low-lying hollow
      in the tile the way the old bathtub did, most visible at `+2m`; (5) the impact
      readout's numbers still rise monotonically with the surge level; (6) with
      `flood.bin` deliberately deleted or renamed, the app still runs and the flood
      layer falls back to the old flat-bathtub behaviour rather than showing nothing or
      erroring; same check with `forecast.json` deleted, falling back to the authored
      week. Not yet checked: whether the `STORM SURGE 风暴潮` title reads oddly on a day
      whose real condition has nothing to do with storms — a small follow-up would be a
      second label (e.g. "PEAK STORM LIKELIHOOD") shown only when `IsPeakStormDay` is
      true but the resolved hour's kind isn't literally Thunderstorm; deliberately not
      built this round to avoid a second UI change on top of an already large one.
- [ ] **Real-hardware controls fix (2026-07-25):** `Tools ▸ WeatherVR ▸ Configure Player
      Settings` first (expect log lines for the loader assignment; a second run should
      report nothing changed), then `Tools ▸ WeatherVR ▸ Build Scene`, then
      `Tools ▸ WeatherVR ▸ Build APK`, install, and check **on the real PICO headset
      this time, not the emulator** — none of this round's fixes touch anything the
      emulator can exercise (no controllers, no PXR native library, no real stage-mode
      behaviour). In order:
      (1) `adb logcat -c && adb shell monkey -p com.weathervr.immersive -c
      android.intent.category.LAUNCHER 1 && adb logcat -v time -s Unity`, grep
      `XRBOOT` — must show `activeLoader=ByteDance.PICO.XR.PXR_Loader` and
      `deviceActive=True` before looking at anything else;
      (2) read the `XRBOOT devices=` dump and note which `chars=` mask and which
      `XRPointer` pose tier actually won (logged separately as
      `XRPointer (RightController) pose source: ...`) — this is the evidence the six
      detection tiers were guessing at;
      (3) the ray is visible, tracks the controller, disappears when it's set down,
      and terminates at the carousel glass rather than through it or short of it;
      (4) every card, both nav arrows, all four flood-surge presets, and a *drag* of
      the time slider all respond to the trigger;
      (5) the table and carousel move together, hold still on a glance, come along
      when you walk, and the secondary/menu button re-centres them — watch
      specifically for the carousel's wall-selection hysteresis hopping between walls
      while the map eases into a new position;
      (6) the glass floor sits at the actual floor, not through your head, across a
      cold launch, a recenter, and (if testable) a room change;
      (7) frame time near 72 FPS with the two new `LineRenderer`s (ray + reticle) and
      a `+10m` flood surge raised during Thunderstorm. If the loader or defines needed
      correcting, `Configure()` throws by design — re-run once more after that.
- [ ] **Map at 1.5× (2026-07-25, branch `bigger-map-1.5x`):** `tools/verify.py` passes
      and compiles the runtime scripts, but nothing here has been through Play mode.
      **`Tools ▸ WeatherVR ▸ Build Scene` is required this round**, not optional: the
      map's `ComfortFollow.Distance` is baked into the scene by `SceneBuilder`, and
      `WeatherSceneBootstrap.EnsureMapFollow` only sets it when adding a *missing*
      component — a scene built before this change keeps 2.45 and will put the carousel
      0.41 m from the user's face. Then Press Play and confirm:
      (1) the table is visibly half again as wide, and the plinth under it is *not* —
      it should still read as the same 0.68 m-tall table, with the map now overhanging
      it by ~36 cm per side (deliberate; the judgement call is whether that overhang
      reads as a cantilevered glass slab or as a mistake, which no amount of maths
      settles — if it reads as a mistake, set `PedestalReferenceMapSizeMeters` equal to
      `MapSizeMeters` and the plinth grows with the map again);
      (2) look at the map's edge from *below* eye level — the terrain's underside is no
      longer covered by the pedestal's interior depth pass, so check whether the
      heightfield reads as see-through/hollow there;
      (3) buildings are noticeably chunkier and taller, and check the dense core around
      the Gherkin/Leadenhall for footprints visibly interpenetrating (expected at
      `BuildingFootprintScale = 1.5`, but it is the first thing to dial back if the
      cluster reads as a solid mass rather than individual towers);
      (4) the tallest tower's tip against the cloud deck — `Verify.cs` reports it ~3 cm
      *inside* the base. Decide on sight whether that reads as "scraping the clouds"
      (fine, arguably good) or as clipping (drop `BuildingHeightExaggeration` toward
      1.25, or raise `WeatherVisuals.CloudBaseMeters` further);
      (5) the carousel is at the same distance and size as before, mounts flush on the
      wall facing you, and the controller ray still hits every card, both arrows, the
      four flood presets and a *drag* of the time slider — the wall mount moved in map
      units this round, and carousel input has broken silently twice in this project;
      (6) walk around the table: the wall-selection hysteresis (`WallSwitchMargin`, in
      map units, so its real threshold grew 1.5× too) should still hop cleanly rather
      than lag a full quarter-turn behind you;
      (7) lightning bolts still land on the terrain rather than stopping short or
      punching through — the bolt channel is built in map units from `_cloudBaseMap`,
      which moved this round;
      (8) on device: frame time near 72 FPS. The larger map means more of the view is
      filled by terrain/buildings/water and the cloud billboards subtend a bigger solid
      angle, so this is a genuine fill-rate increase, not a neutral change — worst case
      is a Thunderstorm with a `+10m` surge raised, viewed from a table edge.

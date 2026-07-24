# Immersive weather carousel

This folder is the complete PICO bottom-carousel feature. It is runtime-built
in `WeatherVR.unity`: press Play, place the London weather map, and the
carousel appears in the lower field of view.

No prefab or hand-authored scene changes are required. The feature hides the
old map-side provenance text panel at runtime, so rebuilding the generated
scene does not undo the replacement.

## UI method

This uses Unity uGUI on a world-space `Canvas`. The hierarchy is created from
small C# builder methods when `WeatherVR` runs, so there is no prefab to wire
up and no scene YAML to conflict during merges. It is data-driven, but it is
not Unity UI Toolkit/UXML-style declarative UI.

## Files

- `WeatherCarouselFeature.cs` — automatic startup, visibility and integration.
- `WeatherCarouselBuilder.cs` — compact bilingual glass layout.
- `WeatherCarouselController.cs` — selection, snapping and animation.
- `WeatherCarouselInput.cs` — existing PICO ray, trigger drag and arrows.
- `WeatherCarouselData.cs` — data model and backend boundary.
- `WeatherCarouselGraphics.cs` — inexpensive glass gradients and vector icons.

## Connect a backend

Implement `IWeatherCarouselDataProvider`, then change the single return line in
`WeatherCarouselDataProvider.CreateDefault()`.

The provider receives the current `WeatherSnapshot` and returns a
`WeatherCarouselDataset`. All backend-specific networking and JSON parsing
should stay in that provider; the UI does not need to change.

The default provider uses the current scene data for the first card and creates
a deterministic short demo projection for the remaining cards.

## Language hierarchy

Every semantic label uses:

1. English — larger, brighter and bold where appropriate.
2. Simplified Chinese — directly underneath, smaller and lower contrast.

At runtime the feature looks for a CJK-capable system font. PICO/Android
normally provides Noto Sans CJK SC; the Windows editor uses Microsoft YaHei
when available.

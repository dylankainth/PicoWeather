# Prebuilt APKs

Sideload-ready builds of **Immersive Weather**. Both render the same scene — a
5 km × 5 km slice of the City of London with real terrain, satellite imagery and
OpenStreetMap building massing, under a weather scene you pick and scrub through
a 24-hour clock — only the rig and the controls differ.

| File | Size | Target | Controls |
| --- | --- | --- | --- |
| `ImmersiveWeather-Phone.apk` | 61 MB | any Android phone | touch |
| `ImmersiveWeather-PICO.apk` | 45 MB | PICO headset / PICO Emulator | controller or hand |

## Phone build — start here

No AR, no headset, no permissions. The map is on screen from the first frame.

| Gesture | Action |
| --- | --- |
| One finger, drag | orbit around the map |
| Two fingers, pinch | zoom |
| Two fingers, drag | pan |

Download the APK on the phone and tap it (you may need to allow installing from
your browser). Or over USB:

```
adb install -r ImmersiveWeather-Phone.apk
```

## PICO build

```
adb install -r ImmersiveWeather-PICO.apk
```

Earlier builds shipped both ARM64 and x86_64 and needed `--abi arm64-v8a` on the
emulator, because PICO's native libraries — including `libopenxr_loader.so` —
ship for ARM64 only, so the x86_64 slice has no XR runtime to composite through
and the app falls back to rendering as a flat 2D panel in the PICO shell. This
build is ARM64-only, so there is no wrong slice left to install and the plain
command is correct everywhere. On the emulator it runs under translation at a
steady 60 FPS; only startup is slow (~30 s to the first frame).

## What you are looking at

The panel beside the map reports its own provenance, because roughly half of what
is rendered is inferred rather than measured:

- **Measured** — terrain (SRTM-derived), imagery (Esri), building footprints and
  heights (OpenStreetMap), and cloud cover, rain, wind and CAPE from Open-Meteo.
- **Derived** — cloud layer altitudes, converted from ECMWF pressure levels.
- **Invented** — the *shape* of the clouds. No operational weather model resolves
  an individual cumulus tower.
- **Derived** — the lightning, from CAPE × rain rate. No free feed publishes
  stroke density.
- **Illustrative** — the flood surge presets. Fixed +2/+5/+10 m water planes, not
  a hydrological model.

These builds ship with `ForceProceduralWeather` on, so there is always a storm to
look at. Real London is usually unremarkable, which is correct and impossible to
demo. The panel says *procedural (demo mode)* when this is active.

## Known limitations

- Neither build has been run on real PICO hardware. The PICO build is verified
  immersive in the PICO Emulator at 60 fps.
- The phone build has never been run on physical hardware by the author — it is
  verified as building correctly and containing the right components.
- An AR variant (ARCore, tap a real surface to place the map) exists on the
  `phone-ar` branch. It was unreliable in testing — plane detection depends on
  your lighting and surface texture — which is why the touch build exists.

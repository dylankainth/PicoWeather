# Prebuilt APKs

Sideload-ready builds of **Immersive Weather**. Both render the same scene — real
Shanghai terrain and satellite imagery, volumetric cloud at true altitudes,
lightning and thunder — only the rig and the controls differ.

| File | Size | Target | Controls |
| --- | --- | --- | --- |
| `ImmersiveWeather-Phone.apk` | 61 MB | any Android phone | touch |
| `ImmersiveWeather-PICO.apk` | 59 MB | PICO headset / PICO Emulator | controller or hand |

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
adb install -r -t --abi arm64-v8a ImmersiveWeather-PICO.apk
```

**The `--abi arm64-v8a` flag matters on the emulator.** The emulator is an x86_64
image, but PICO's native libraries — including `libopenxr_loader.so` — ship for
ARM64 only. Install the x86_64 slice and you get a running app with a black
screen, because there is no XR runtime to composite through. On real hardware the
plain `adb install -r` is fine.

## What you are looking at

The panel beside the map reports its own provenance, because roughly half of what
is rendered is inferred rather than measured:

- **Measured** — terrain (SRTM-derived), imagery (Esri), and cloud cover, rain,
  wind and CAPE from Open-Meteo.
- **Derived** — cloud layer altitudes, converted from ECMWF pressure levels.
- **Invented** — the *shape* of the clouds. No operational weather model resolves
  an individual cumulus tower.
- **Derived** — the lightning, from CAPE × rain rate. No free feed publishes
  stroke density.

These builds ship with `ForceProceduralWeather` on, so there is always a storm to
look at. Real Shanghai is usually clear, which is correct and impossible to demo.
The panel says *procedural (demo mode)* when this is active.

## Known limitations

- The volumetric cloud raymarch is heavy. The performance governor trades cloud
  quality for frame rate automatically, so clouds may look softer on a phone than
  in the renders.
- The phone build has never been run on physical hardware by the author — it is
  verified as building correctly and containing the right components.
- An AR variant (ARCore, tap a real surface to place the map) exists on the
  `phone-ar` branch. It was unreliable in testing — plane detection depends on
  your lighting and surface texture — which is why the touch build exists.

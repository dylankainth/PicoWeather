# Demo run-book

Two to three minutes, plus what to do when it goes wrong.

---

## Before the room fills up

```powershell
python tools/build_all.py          # bake fresh data (needs network)
python tools/verify.py             # 35 checks, ~1 s, catches a bad bake
```

Then in Unity: **Tools ▸ WeatherVR ▸ Build Scene**, press Play once to confirm
it comes up, then **Tools ▸ WeatherVR ▸ Build APK**.

```powershell
adb install -r Builds/ImmersiveWeather.apk
```

**Decide the weather before you start.** Open
`Assets/WeatherVR/Resources/WeatherVRConfig.asset`:

- `ForceProceduralWeather = false` — real Shanghai, right now. Honest, and
  frequently a clear sky with no lightning whatsoever.
- `ForceProceduralWeather = true` — the synthetic squall line. Always
  demonstrable. Still labelled "procedural (demo mode)" on the panel.

Check the real weather first. If Shanghai is clear, use demo mode and *say so* —
the panel says it anyway, and being the one to point it out is much better than
being asked.

---

## The walkthrough

**1 — Place it.** Point at the table, pull the trigger (or pinch).

> "That's a fifty-kilometre square of Shanghai, on your table at 1:25,000."

**2 — The ground.** Let them look down at it first.

> "Satellite imagery over real elevation. That's the Huangpu winding through
> the centre, the Yangtze opening into the estuary at the top right. The delta
> is genuinely this flat — the median elevation across the whole tile is about
> four metres."

**3 — The air.** Get them to look *up*, into the volume.

> "The cloud isn't a texture on a ceiling — it's a volume with real altitude.
> That deck is sitting where the model says it is, converted from pressure
> levels. Put your head in it."

Do encourage that. Walking into the storm is the moment the whole thing lands,
and it's the thing a screenshot can never convey.

**4 — The lightning.** Wait for a strike rather than talking over one.

> "Strikes cluster where the atmosphere is actually unstable and wet, and the
> flash lights the cloud from inside — that's the volume being lit, not a
> sprite. Thunder is generated per strike for its distance, so the far ones
> rumble and the near ones crack."

**5 — Walk around it.** Change your angle mid-sentence; let them notice the
parallax doing the work.

**6 — Close on the honesty.**

> "The terrain, imagery, cloud cover, rain and instability are all real
> measurements. The cloud *shapes* are procedural — no operational model
> resolves a single cumulus tower. And the lightning is derived from
> instability and rain rate, because no free feed publishes strike locations.
> The panel says which is which."

That last beat is worth keeping. It is the difference between a demo and a
claim, and technical audiences respect it.

---

## If something goes wrong

| Symptom | Cause | Fix |
| --- | --- | --- |
| View doesn't follow your head | Scene predates the pose-driver fix | It self-repairs at runtime and warns; rebuild the scene to silence it |
| No lightning at all | Real weather is calm — correct behaviour | `ForceProceduralWeather = true` |
| Everything says "procedural" | Bake missing or network down | `python tools/build_all.py` |
| Clouds invisible | Camera lost its depth texture, or shader stripped | Check console for "shader is missing"; run **Add Shaders to Always-Included** |
| Clouds render over the terrain | Depth texture unavailable | Confirm `depthTextureMode` on the camera |
| Frame rate soft | Governor is already shedding steps | Check the panel — it shows fps and the current tier |
| Map placed somewhere silly | Mis-aimed at commit time | Hold grip ~1 s to re-place |

**The one to rehearse:** if the venue Wi-Fi is down, nothing breaks — every
layer falls back to procedural generation and the app still runs. Bake the data
beforehand anyway, because baked real terrain and imagery are much more
convincing than the synthesised versions.

---

## Questions worth having an answer ready for

**"Is this real-time?"** The weather is a snapshot fetched at launch, not a
live feed. Cloud motion is the wind field advecting detail, exaggerated so it's
perceptible — at true scale a 2 m map would take hours to visibly change.

**"How accurate are the cloud heights?"** The layer boundaries are real,
converted from ECMWF pressure levels with the standard barometric formula. How
full each layer is, is real. The shape inside it is not.

**"Why is the terrain so exaggerated?"** Vertical is ×4 relative to horizontal,
and relief gets a further boost. At true scale the Yangtze delta is a flat
sheet — accurate and completely unreadable. One knob, `VerticalExaggeration`,
set it to 1 to see honest scale.

**"Could it do my city?"** Change `CenterLatitude`/`CenterLongitude` on the
config asset and re-bake. Nothing else is Shanghai-specific — the data sources
are all global.

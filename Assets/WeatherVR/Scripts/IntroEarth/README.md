# Earth to London intro

This folder is the complete feature. It does not modify the generated
`WeatherVR.unity` scene or any existing WeatherVR runtime component.

At launch:

1. The existing London map and carousel are temporarily hidden.
2. A texture-free, low-poly Earth appears in front of the headset.
3. The globe rotates until the orange London marker faces the user.
4. The globe zooms through London and fades.
5. The existing London map and carousel are restored unchanged.

Asset budget:

- One 320-triangle sphere; per-vertex colours suggest land and ocean
- 448 coordinate-line vertices
- An 8-triangle London marker
- Three tiny runtime materials
- No bitmap textures, downloads, prefabs or external models

The generated mesh data is uploaded to the GPU as non-readable, allowing Unity
to release its CPU-side copy.

Preview the exact runtime asset with:

`Tools > WeatherVR > Render Earth Intro Preview`

The resulting image is written to `Builds/earth-intro-preview.png`.

To remove the feature, delete this folder and its neighbouring
`IntroEarth.meta` file.

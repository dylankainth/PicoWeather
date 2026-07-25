// Shared building blocks for every hand-written "liquid glass" surface in the
// project (currently Pedestal.shader and GlassSurround.shader). Kept as a .cginc
// rather than a shader so it carries no asset of its own -- nothing to register
// in the always-included shader list, nothing that can be stripped.
#ifndef WEATHERVR_GLASS_CGINC
#define WEATHERVR_GLASS_CGINC

// ---------------------------------------------------------------------------
// One anti-aliased line set, analytic. `coord` is a position already divided by
// the tick spacing, so a line sits at every integer value of coord.
//
// Three things are happening here, and all three are load-bearing for a scene
// with no post-process AA that has to survive grazing VR viewing angles:
//
//  fw       how many tick periods this single pixel covers. This is the number
//           that explodes at grazing angles and is the root cause of moire.
//  hw       half-width derived FROM fw, so the line stays a fixed number of
//           *pixels* wide instead of a fixed number of periods. Without this
//           the line collapses to a sub-pixel sliver that strobes as the head
//           moves -- the single worst artifact this function exists to avoid.
//  resolve  fades the whole line set out once one pixel covers more than about
//           half a period. Past that point the signal is genuinely
//           unresolvable and flat colour is the only stable answer.
//
// 4x MSAA does not substitute for `resolve` -- MSAA antialiases geometry
// edges; this is shading-frequency aliasing, which MSAA samples once per pixel
// same as anything else.
// ---------------------------------------------------------------------------
half AxisLines(float coord, half widthPixels)
{
    half fw = max(fwidth(coord), 1e-5h);
    half d = abs(frac(coord + 0.5) - 0.5);
    half hw = fw * widthPixels * 0.5h;
    half resolve = saturate(1.0h - fw * 2.0h);
    return resolve * (1.0h - smoothstep(hw, hw + fw, d));
}

// ---------------------------------------------------------------------------
// The kaleidoscope, in exactly one place.
//
// Every surface in the surround folds through this same wedge layout -- the
// skybox, the glass floor's radial spokes, and the pedestal's reflections -- so
// the sky's mirror seams, the floor's spokes and the plinth's eight physical
// facets all share ONE symmetry. They did not before: the skybox folded into 12
// wedges with its own hand-rolled copy of this maths while the glass folded into
// 8, which is why a facet's reflection never looked like it belonged to the sky
// above it. Same class of silent drift as the sky palette, fixed the same way --
// one definition, read by everyone (see EnvironmentController's header).
//
// Segment count and spin arrive as globals (_WVRKaleidoSegments/_WVRKaleidoSpin,
// published by EnvironmentController) rather than per-material properties, for
// the reason spelled out in Pedestal.shader: a Properties entry carries a
// per-material default that silently shadows the global.
// ---------------------------------------------------------------------------

// Resolved here so no two surfaces can round it differently. Rounded to an
// integer because a fractional count makes the wedge coordinate discontinuous
// where the azimuth wraps at +-pi -- frac() would step by a non-integer there,
// showing up as one bad seam in an otherwise regular pattern. The floor of 3 is
// both the smallest count that is a kaleidoscope rather than a mirror pair and
// the guard on the unset-global case: an unpublished global reads 0, and
// 2*pi/0 is not a wedge.
half KaleidoSegments(half segments)
{
    return max(round(segments), 3.0h);
}

// Position along the wedge layout, measured in wedges, so integer values land
// exactly on the mirror seams. Continuous across the azimuth wrap (given an
// integer segment count), which is what lets it be fed straight to AxisLines to
// draw those seams as pixel-width lines.
float KaleidoCell(float2 xz, half segments, half phase)
{
    return (atan2(xz.y, xz.x) + phase) * KaleidoSegments(segments) / 6.2831853;
}

// One full decomposition from a single atan2. `dir` must be unit length.
// Elevation (y) is deliberately left unfolded: folding it too reads as noise
// rather than a prism, and it tilts the horizon off level.
struct WvrKaleido
{
    float3 dir;     // azimuth folded into one mirrored wedge
    float  cell;    // see KaleidoCell -- integers are the mirror seams
    half   wedge;   // 0 on the seam .. 1 at the wedge centre
    half   seam;    // 1 on the seam, analytic falloff -- the glass edge itself
    half   radial;  // 0 looking straight up or down, 1 at the horizon
};

WvrKaleido KaleidoSplit(float3 dir, half segments, half phase)
{
    WvrKaleido k;
    half segs = KaleidoSegments(segments);

    // For a unit direction length(dir.xz) is cos(elevation), which is already
    // exactly the horizon weight every caller wants -- so it falls out of the
    // fold for free here instead of being recomputed as sqrt(1 - y*y) downstream.
    float r = length(dir.xz);
    k.cell = KaleidoCell(dir.xz, segs, phase);

    // Mirroring alternate wedges is what makes this a kaleidoscope rather than a
    // rotational repeat: neighbours show mirror-image slices of the environment,
    // not the same slice turned.
    k.wedge = abs(frac(k.cell) * 2.0h - 1.0h);

    float fa = k.wedge * (6.2831853 / segs);
    k.dir = float3(cos(fa) * r, dir.y, sin(fa) * r);
    k.seam = 1.0h - smoothstep(0.0h, 0.09h, k.wedge);
    k.radial = saturate(r);
    return k;
}

// Mirror-symmetric spectral split -- the prism half of the theme, and the one
// operation that makes a fold read as glass rather than as a mirror.
//
// Driven by the within-wedge coordinate rather than a wedge index, which buys
// two things at once: symmetric facet pairs come out the same colour (what a
// real kaleidoscope does, since the chips are shared between mirrored views),
// and the hue stays continuous across every seam. The second one is a comfort
// requirement rather than an aesthetic preference -- a per-wedge hue steps
// discontinuously as the pattern spins, sweeping a hard colour edge through
// peripheral vision, which is the single thing this whole surround is tuned to
// avoid.
half3 KaleidoDisperse(half3 colour, half wedge, half strength)
{
    half3 split = cos(6.2831853h * (wedge * 0.5h + half3(0.0h, 0.33h, 0.67h)));
    return colour * (1.0h + split * strength);
}

// Cheap hand-rolled exponential-squared fog, matching Unity's built-in
// FogMode.ExponentialSquared exactly. Unity uploads unity_FogParams /
// unity_FogColor as globals whether or not any FOG_* keyword is compiled in,
// so this costs one exp2() and adds ZERO shader variants -- which matters
// specifically because every material touching this include is created at
// runtime via Shader.Find with no material asset backing it, so
// multi_compile_fog variants would have nothing to keep them alive and could
// silently go missing on device.
//   unity_FogParams.x is density / sqrt(ln 2), i.e. the exp2-form density.
half3 ApplyWvrFog(half3 colour, float viewDistance)
{
    half fd = unity_FogParams.x * viewDistance;
    half fog = saturate(exp2(-fd * fd));
    return lerp(unity_FogColor.rgb, colour, fog);
}

#endif // WEATHERVR_GLASS_CGINC

// Liquid-glass ".xyz" plinth the tabletop map stands on.
//
// Two passes over the SAME closed mesh fake glass thickness with no framebuffer
// read at all: pass 1 (Cull Front, opaque, ZWrite On) draws the far wall of the
// shell from the inside, so the pedestal is never see-through to nothing --
// the terrain is a single heightfield with no underside, and an all-transparent
// plinth would show the skybox straight through the map. Pass 2 (Cull Back,
// premultiplied blend, ZWrite Off) draws the near wall over it. Same object,
// same draw call, so the composite order is guaranteed by pass declaration --
// not by any distance sort -- which is what makes this safe against the
// Water shader's own Transparent queue (see the Queue note below).
//
// The coordinate motif (ticks/graticule) is drawn on BOTH walls, so the far
// wall's motif parallaxes visibly behind the near wall's as the head moves --
// that parallax, more than any single shading trick, is what reads as "thick
// glass" rather than "a tinted decal".
//
// Kaleidoscope facets fold the reflected environment through KaleidoSplit --
// shared with the skybox and the glass floor, so all three carry one symmetry --
// and split it with KaleidoDisperse plus a per-channel fresnel, a cheap stand-in
// for dispersion. The whole effect is gated by the _WVRKaleidoAmount global
// (0..1, published by EnvironmentController from AppConfig.PedestalKaleidoscope):
// at 0 this is a plain, finished ".xyz" glass plinth with a single unfolded
// reflection -- reverting the accent is a slider, never a code change.
Shader "WeatherVR/Pedestal"
{
    Properties
    {
        [Header(Body)]
        _BaseColor   ("Base Color", Color) = (0.022, 0.020, 0.030, 1)
        _AmbientMix  ("Ambient Contribution", Range(0, 1)) = 0.9

        [Header(Glass)]
        _GlassAlpha  ("Glass Alpha", Range(0, 1)) = 0.42
        _Thickness   ("Fake Thickness", Range(0, 4)) = 1.6

        [Header(Rim Glow)]
        _RimColor    ("Rim Glow Color", Color) = (0.46, 0.38, 0.50, 1)
        _RimPower    ("Fresnel Power", Range(0.5, 8)) = 3.5
        _RimStrength ("Fresnel Rim Strength", Range(0, 4)) = 0.10

        [Header(Lip Band)]
        _BandStrength ("Vertex Glow Band Strength", Range(0, 4)) = 1.4

        [Header(Specular)]
        _SpecStrength ("Specular Strength", Range(0, 8)) = 2.2
        _SpecSharp    ("Specular Sharpness", Range(1, 256)) = 64

        [Header(XYZ Coordinate Motif)]
        _TickSpacing  ("Tick Spacing (map units)", Range(0.005, 0.2)) = 0.025
        _MajorEvery   ("Major Tick Every N", Range(2, 10)) = 4
        _TickWidthPx  ("Tick Width (pixels)", Range(0.5, 4)) = 1.4
        _GridStrength ("Motif Strength", Range(0, 3)) = 1.0
        _GridColor    ("Motif Color", Color) = (0.55, 0.85, 1.0, 1)
        _AxisTintX    ("X Axis Tint", Color) = (1.0, 0.35, 0.35, 1)
        _AxisTintY    ("Y Axis Tint", Color) = (0.40, 1.0, 0.50, 1)
        _AxisTintZ    ("Z Axis Tint", Color) = (0.35, 0.55, 1.0, 1)

        // Amount/segments/spin are NOT here on purpose: they arrive as _WVRKaleido*
        // globals (published by EnvironmentController from
        // AppConfig.PedestalKaleidoscope) so the plinth, the floor and the sky
        // cannot fold differently. A Properties entry would carry a per-material
        // default that silently shadows the global -- the same trap the sky palette
        // globals below are commented for.
        [Header(Kaleidoscope Accent)]
        _Dispersion      ("Dispersion", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+450" "IgnoreProjector" = "True" }
        LOD 100

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "Lighting.cginc"
        #include "WeatherVRGlass.cginc"
        #include "StudioSkyGradient.cginc"

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            fixed4 color  : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 pos      : SV_POSITION;
            float3 worldPos : TEXCOORD0;
            float3 worldNrm : TEXCOORD1;
            float3 objPos   : TEXCOORD2;
            fixed4 color    : COLOR;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        fixed4 _BaseColor;
        half _AmbientMix;
        half _GlassAlpha;
        half _Thickness;
        fixed4 _RimColor;
        half _RimPower;
        half _RimStrength;
        half _BandStrength;
        half _SpecStrength;
        half _SpecSharp;

        half _TickSpacing;
        half _MajorEvery;
        half _TickWidthPx;
        half _GridStrength;
        fixed4 _GridColor;
        fixed4 _AxisTintX;
        fixed4 _AxisTintY;
        fixed4 _AxisTintZ;

        half _Dispersion;

        // Published once per weather switch by EnvironmentController.Apply.
        // Deliberately declared here and NOT in Properties: a Properties entry
        // gets a per-material default that would silently shadow this global, so
        // the pedestal would freeze at the shader's default palette instead of
        // following the sky. Distinct _WVR names avoid any collision with
        // StudioSky.shader's own _Zenith/_Horizon/etc material properties.
        half4 _WVRSkyZenith, _WVRSkyHorizon, _WVRSkyNadir, _WVRSunColor;
        half _WVRSkySharpness, _WVRSunGlow, _WVRSunSharp;
        float4 _WVRSunDir;
        half _WVRKaleidoAmount, _WVRKaleidoSegments, _WVRKaleidoSpin;

        v2f vert(appdata v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            o.pos = UnityObjectToClipPos(v.vertex);
            o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.worldNrm = UnityObjectToWorldNormal(v.normal);
            o.objPos = v.vertex.xyz;
            o.color = v.color;
            return o;
        }

        // The .xyz instrument face. `nrm` is a flat per-facet normal (baked by
        // PedestalMeshBuilder), so picking the dominant axis is exact rather than a
        // blend across a smooth surface. `vcol.a` (1 on side bands, 0 on caps)
        // switches between the Y-axis ladder and the XZ graticule with one lerp.
        half3 CoordinateMotif(float3 objPos, half3 nrm, fixed4 vcol, half viewFacing)
        {
            half px = _TickWidthPx;

            // Major spacing widens slightly toward the base (vcol.b -> 1) so the
            // ladder reads as coarser further from the crown, like a depth scale.
            half majorEvery = _MajorEvery * (1.0h + vcol.b * 0.5h);
            float yc = objPos.y / _TickSpacing;
            half minorY = AxisLines(yc, px);
            half majorY = AxisLines(yc / majorEvery, px * 1.6h);
            half ladder = (minorY * 0.35h + majorY) * vcol.a;

            half grid = max(AxisLines(objPos.x / _TickSpacing, px),
                             AxisLines(objPos.z / _TickSpacing, px)) * (1.0h - vcol.a);

            // One bright accent stripe down the centre of the facets that face
            // +-X and +-Z respectively -- the ".xyz" identity itself, four
            // AxisLines evaluations total.
            half onX = saturate(abs(nrm.x) * 2.0h - 1.0h);
            half onZ = saturate(abs(nrm.z) * 2.0h - 1.0h);
            half big = _TickSpacing * _MajorEvery * 4.0h;
            half stripeX = AxisLines(objPos.z / big, px * 2.0h) * onX;
            half stripeZ = AxisLines(objPos.x / big, px * 2.0h) * onZ;

            half3 col = _GridColor.rgb * (ladder + grid);
            col += _AxisTintX.rgb * stripeX + _AxisTintZ.rgb * stripeZ;
            col += _AxisTintY.rgb * majorY * vcol.a * 0.25h;

            // The motif is weakest right on the near silhouette -- exactly where
            // fwidth is worst -- so hand those pixels to the fresnel rim instead
            // of fighting it for them.
            return col * _GridStrength * viewFacing;
        }

        // Shared body for both passes. `isInterior` selects the ambient-only far
        // wall (Cull Front, no LightMode -- see the Pass comment below) versus the
        // fully lit, blended near wall.
        fixed4 GlassBody(v2f i, bool isInterior)
        {
            float3 normal = normalize(i.worldNrm);
            // Cull Front renders the mesh's far wall from inside a closed shell;
            // its authored normal still points outward (away from camera), so it
            // must be flipped to read as an inward-facing surface -- otherwise the
            // interior would shade as if lit from behind itself.
            if (isInterior) normal = -normal;

            float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
            half ndv = saturate(dot(normal, viewDir));

            fixed3 ambientLit = _BaseColor.rgb * unity_AmbientSky.rgb * _AmbientMix;

            // Fake thickness: path length through a flat slab is 1/cos(theta), so
            // 1/ndv is the physically-shaped curve that thickens the glass toward
            // its silhouette with no framebuffer read and no depth sample.
            half path = rcp(max(ndv, 0.10h));
            half alpha = saturate(_GlassAlpha * (1.0h + (path - 1.0h) * _Thickness));

            // Triple-power fresnel: the same edge falloff at three exponents mapped
            // to R/G/B stands in for chromatic dispersion at a fraction of the ALU
            // cost of evaluating the reflected environment three times.
            half f = pow(1.0h - ndv, _RimPower);
            half3 fres3 = half3(pow(1.0h - ndv, _RimPower * 0.80h), f, pow(1.0h - ndv, _RimPower * 1.30h));
            fres3 = lerp(f.xxx, fres3, _Dispersion * _WVRKaleidoAmount);

            // The environment reflection, folded through the shared wedge layout --
            // the same one the skybox and the glass floor use, and by default the
            // same count as this mesh's own 8 facets, so the effect reads as "these
            // facets are splitting the light" rather than an unrelated swirl.
            //
            // No per-facet phase offset any more. It used to add i.color.g * 2pi,
            // randomising each facet's slice, which looked busy but destroyed the
            // very alignment that makes the effect legible: with the surround folded
            // into the same eight wedges the plinth physically has, each facet's own
            // normal already lands it on a different part of the pattern, and the
            // seams it reflects line up with the seams in the sky above it. (The
            // mesh still writes the per-facet hash into COLOR.g; it is simply no
            // longer needed here.)
            float3 refl = reflect(-viewDir, normal);
            WvrKaleido k = KaleidoSplit(refl, _WVRKaleidoSegments,
                                        _Time.y * _WVRKaleidoSpin);
            float3 kdir = normalize(lerp(refl, k.dir, _WVRKaleidoAmount));
            half3 env = StudioSkyColour(kdir, _WVRSkyZenith.rgb, _WVRSkyHorizon.rgb, _WVRSkyNadir.rgb,
                                         _WVRSkySharpness, _WVRSunColor.rgb, _WVRSunGlow, _WVRSunSharp,
                                         normalize(_WVRSunDir.xyz));

            // Split the reflected sky across the wedge with the shared helper, so
            // plinth, floor and sky disperse in step instead of each carrying its
            // own tint. The triple-power fresnel above shapes *where* the colour
            // lands; this decides what colour it is.
            env = KaleidoDisperse(env, k.wedge, _Dispersion * _WVRKaleidoAmount);

            fixed3 rim = (_RimColor.rgb + env) * fres3 * _RimStrength;

            // Vertex-painted glow band -- emissive, independent of view angle, so
            // the crown glows steadily rather than only at grazing angles.
            fixed3 band = _RimColor.rgb * i.color.r * _BandStrength;

            half viewFacing = saturate(ndv * 1.4h);
            fixed3 motif = CoordinateMotif(i.objPos, normal, i.color, viewFacing);

            fixed3 emissive = rim + band + motif;

            if (isInterior)
            {
                // Ambient-only: no LightMode tag means this pass has no reliable
                // per-object directional-light binding, so it deliberately never
                // reads _WorldSpaceLightPos0 / _LightColor0. Fully opaque, writes
                // depth -- see the header comment for why that matters.
                return fixed4(ambientLit + emissive, 1.0h);
            }

            float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
            half ndl = saturate(dot(normal, lightDir));
            fixed3 lit = ambientLit + _BaseColor.rgb * _LightColor0.rgb * ndl;

            float3 halfDir = normalize(lightDir + viewDir);
            half spec = pow(saturate(dot(normal, halfDir)), _SpecSharp) * _SpecStrength;
            emissive += spec * _LightColor0.rgb;

            // Premultiplied output: `lit` is what the glass transmits, scaled by
            // alpha; `emissive` (rim/band/motif/specular) is light the glass adds
            // and stays at full strength regardless of alpha. Straight
            // SrcAlpha/OneMinusSrcAlpha blending would multiply those highlights
            // down by a ~0.4 alpha, which is exactly why faked glass usually reads
            // as grey plastic instead of something lit from within.
            return fixed4(lit * alpha + emissive, alpha);
        }
        ENDCG

        Pass
        {
            // Interior / far wall. See the header + GlassBody comments for why
            // this pass carries no LightMode tag and must run first.
            Cull Front
            ZWrite On
            ZTest LEqual
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragInterior
            #pragma multi_compile_instancing
            #pragma target 3.0

            fixed4 fragInterior(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return GlassBody(i, true);
            }
            ENDCG
        }

        Pass
        {
            // Glass skin / near wall, premultiplied over the interior pass above.
            Tags { "LightMode" = "ForwardBase" }
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGlass
            #pragma multi_compile_instancing
            #pragma target 3.0

            fixed4 fragGlass(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return GlassBody(i, false);
            }
            ENDCG
        }
    }

    // No fallback: a plausible-looking flat grey box on SubShader failure would
    // hide a real problem instead of surfacing it (see Water.shader / IntroEarth.shader,
    // which take the same position).
    Fallback Off
}

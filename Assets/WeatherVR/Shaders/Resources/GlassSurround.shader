// World-locked glass floor: the ".xyz" instrument ground the plinth stands on.
// Opaque, not blended, and that is deliberate: the camera's clearFlags is
// Skybox, so a solid floor written to depth BEFORE the skybox draws gets those
// pixels z-rejected out of the skybox pass for free -- the floor *replaces*
// fill cost across roughly the bottom third of the view rather than adding a
// blended layer on top of it. A translucent floor was considered and rejected
// specifically because it would be the single biggest fill-rate risk in the
// whole redesign (see CLAUDE.md's pedestal/scenery notes).
//
// Placed in a Resources/ folder (mirroring IntroEarth.shader) and loaded via
// Resources.Load rather than relying solely on the always-included shader
// list: Resources assets are never stripped from a build, which is belt and
// braces against the exact silent, device-only null-shader bug this project
// has already hit once (see ProjectConfigurator.cs).
Shader "WeatherVR/GlassSurround"
{
    Properties
    {
        [Header(Base)]
        _FloorColor  ("Floor Color", Color) = (0.05, 0.07, 0.11, 1)
        _RimColor    ("Under-table Glow", Color) = (0.40, 0.72, 1.0, 1)

        [Header(Grid)]
        _GridColor    ("Grid Color", Color) = (0.45, 0.68, 0.92, 1)
        _GridSpacing  ("Minor Grid Spacing (m)", Range(0.05, 2)) = 0.25
        _RingSpacing  ("Range Ring Spacing (m)", Range(0.25, 5)) = 1.0
        _GridWidthPx  ("Grid Line Width (px)", Range(0.5, 4)) = 1.3
        _GridStrength ("Grid Strength", Range(0, 2)) = 0.9

        [Header(XYZ Axes)]
        _AxisTintX ("X Axis Tint", Color) = (1.0, 0.35, 0.35, 1)
        _AxisTintZ ("Z Axis Tint", Color) = (0.35, 0.55, 1.0, 1)
        _AxisWidthPx ("Axis Line Width (px)", Range(1, 6)) = 2.5

        [Header(Falloff)]
        _FadeRadius ("Fade Radius (m)", Range(1, 20)) = 5.0
        _HalfSize   ("Plane Half Size (m)", Range(1, 20)) = 7.0

        // Amount/segments/spin are NOT here on purpose -- they arrive as
        // _WVRKaleido* globals so the floor, the sky and the plinth cannot fold
        // differently. A Properties entry would carry a per-material default that
        // silently shadows the global (see Pedestal.shader).
        [Header(Kaleidoscope Accent)]
        _SpokeStrength   ("Mirror Spoke Strength", Range(0, 2)) = 1.0
        _ReflectStrength ("Sky Reflection Strength", Range(0, 1)) = 0.42
        _Dispersion      ("Reflection Dispersion", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry-100" }
        Cull Back
        ZWrite On
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "../WeatherVRGlass.cginc"
            #include "../StudioSkyGradient.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 objPos   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            fixed4 _FloorColor, _RimColor, _GridColor, _AxisTintX, _AxisTintZ;
            half _GridSpacing, _RingSpacing, _GridWidthPx, _GridStrength, _AxisWidthPx;
            half _FadeRadius, _HalfSize;
            half _SpokeStrength, _ReflectStrength, _Dispersion;

            // Shared surround symmetry -- see the Properties note above.
            half _WVRKaleidoAmount, _WVRKaleidoSegments, _WVRKaleidoSpin;

            // Published once per weather switch by EnvironmentController.Apply --
            // see Pedestal.shader for why these are globals rather than Properties.
            half4 _WVRSkyZenith, _WVRSkyHorizon, _WVRSkyNadir, _WVRSunColor;
            half _WVRSkySharpness, _WVRSunGlow, _WVRSunSharp;
            float4 _WVRSunDir;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 scaled = v.vertex.xyz * _HalfSize;
                o.pos = UnityObjectToClipPos(float4(scaled, 1.0));
                o.worldPos = mul(unity_ObjectToWorld, float4(scaled, 1.0)).xyz;
                o.objPos = scaled;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 xz = i.objPos.xz;
                float r = length(xz);
                half fade = saturate(1.0h - r / _FadeRadius);

                // Cartesian near field, kaleidoscope mid field. The square grid is
                // what makes the ground legible as an instrument right under the
                // table, so it keeps the tighter fade^2; the radial spokes carry
                // further out, where the wedge symmetry is what the eye actually
                // reads. Every line set fades with radius before its own period can
                // alias -- the primary defence against grazing-angle shimmer on a
                // 14 m plane.
                half near = fade * fade;
                half grid = max(AxisLines(xz.x / _GridSpacing, _GridWidthPx),
                                 AxisLines(xz.y / _GridSpacing, _GridWidthPx));
                half rings = AxisLines(r / _RingSpacing, _GridWidthPx);

                // Radial mirror spokes on the same wedge layout as the sky and the
                // plinth's facets -- this is what ties the ground into the fold
                // instead of leaving a square grid under a kaleidoscope sky.
                //
                // Deliberately NOT spun (phase 0 while the reflection below does
                // spin): a slowly rotating structural line across the floor is
                // textbook vection, and the floor fills the lower field of view
                // where that is worst. The moving part of the effect belongs in the
                // reflection, where it is a highlight rather than an edge.
                //
                // No special case needed at r = 0 where the angular period
                // collapses: AxisLines' own `resolve` term sees fwidth explode and
                // fades the spokes out, which is exactly the right answer there.
                half spokes = AxisLines(KaleidoCell(xz, _WVRKaleidoSegments, 0.0h),
                                        _GridWidthPx * 1.4h)
                              * _SpokeStrength * _WVRKaleidoAmount;

                half motif = max(max(grid, rings) * near, spokes * fade);

                // The .xyz ground axes: heavier tinted lines running under the
                // table along X and Z.
                half axisX = AxisLines(xz.y / (_RingSpacing * 100.0h), _AxisWidthPx) * fade;
                half axisZ = AxisLines(xz.x / (_RingSpacing * 100.0h), _AxisWidthPx) * fade;

                half3 col = lerp(_FloorColor.rgb, _GridColor.rgb, motif * _GridStrength);
                col += _AxisTintX.rgb * axisX + _AxisTintZ.rgb * axisZ;

                // A soft pool of light directly under the table -- shadows are
                // disabled project-wide, so this is the only thing grounding the
                // pedestal visually.
                half pool = pow(saturate(1.0h - r / 0.9h), 3.0h);
                col += _RimColor.rgb * pool;

                // Kaleidoscope sky reflection -- a glass floor is exactly the
                // surface that should mirror the sky, so this is where the fold
                // pays off most. It still stays under half strength so the ruled
                // lines above read as an instrument rather than drowning in mirror.
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 refl = reflect(-viewDir, float3(0, 1, 0));
                WvrKaleido k = KaleidoSplit(refl, _WVRKaleidoSegments,
                                            _Time.y * _WVRKaleidoSpin);
                float3 kdir = normalize(lerp(refl, k.dir, _WVRKaleidoAmount));
                half3 sky = StudioSkyColour(kdir, _WVRSkyZenith.rgb, _WVRSkyHorizon.rgb, _WVRSkyNadir.rgb,
                                             _WVRSkySharpness, _WVRSunColor.rgb, _WVRSunGlow, _WVRSunSharp,
                                             normalize(_WVRSunDir.xyz));

                // Same dispersion helper as the sky and the plinth, so the three
                // surfaces split light identically instead of each inventing a tint.
                sky = KaleidoDisperse(sky, k.wedge, _Dispersion * _WVRKaleidoAmount);

                // A brighter line where the reflected wedges meet: the floor's own
                // mirror seams, which move with the reflection rather than with the
                // static spokes above, so the surface reads as glass over an
                // instrument face rather than as a printed pattern.
                col += _RimColor.rgb * k.seam * 0.18h * fade * _WVRKaleidoAmount;

                col = lerp(col, sky, _ReflectStrength * fade);

                col = ApplyWvrFog(col, length(_WorldSpaceCameraPos - i.worldPos));

                return fixed4(col, 1.0h);
            }
            ENDCG
        }
    }

    Fallback Off
}

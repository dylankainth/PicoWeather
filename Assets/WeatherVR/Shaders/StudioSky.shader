// Calm kaleidoscope environment for the tabletop exhibit.
//
// The pattern is analytic and deliberately low-frequency: broad mirrored facets
// give the surround a prismatic identity without creating fast, high-contrast
// detail in peripheral vision. Both hemispheres use the same atmospheric blend,
// so there is no hard horizon or dark "floor" under the table.
//
// The fold itself lives in WeatherVRGlass.cginc and is shared with the glass
// floor and the plinth. This shader used to carry its own hand-rolled copy at a
// different segment count, so the sky's wedges and the wedges reflected in the
// glass disagreed -- see that file's header. The one real payoff of sharing it
// is below: the gradient is sampled along the FOLDED direction, so the sun
// resolves once per wedge and the sky gains mirrored *content* rather than the
// mirrored brightness ripple it had before.
Shader "WeatherVR/StudioSky"
{
    Properties
    {
        _Zenith     ("Zenith",  Color) = (0.08, 0.055, 0.125, 1)
        _Horizon    ("Horizon", Color) = (0.19, 0.125, 0.215, 1)
        _Nadir      ("Nadir",   Color) = (0.03, 0.024, 0.045, 1)
        _HorizonSharpness ("Vertical Blend", Range(0.5, 6)) = 1.45

        _SunColor   ("Sun Glow Color", Color) = (0.72, 0.78, 0.74, 1)
        _SunGlow    ("Sun Glow Strength", Range(0, 4)) = 0.75
        _SunSharp   ("Sun Glow Sharpness", Range(1, 200)) = 12
        _SunDir     ("Sun Direction", Vector) = (0.3, 0.15, -0.9, 0)

        _PrismColor    ("Prism Accent", Color) = (0.52, 0.26, 0.57, 1)
        _PrismStrength ("Prism Strength", Range(0, 1)) = 0.24
        _LatticeStrength ("Lattice Strength", Range(0, 1)) = 0.06
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "WeatherVRGlass.cginc"
            #include "StudioSkyGradient.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            half4 _Zenith, _Horizon, _Nadir, _SunColor, _PrismColor;
            half _HorizonSharpness, _SunGlow, _SunSharp;
            half _PrismStrength, _LatticeStrength;
            float4 _SunDir;

            // The shared surround symmetry, published by EnvironmentController.
            // Globals rather than Properties so the skybox cannot drift from the
            // glass that reflects it -- a Properties entry would carry a
            // per-material default that silently shadows the global.
            half _WVRKaleidoAmount, _WVRKaleidoSegments, _WVRKaleidoSpin;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 dir = normalize(i.dir);
                float3 sun = normalize(_SunDir.xyz);

                // A quarter of the shared spin rate, and that fraction is a comfort
                // decision rather than a taste one. The fold below now puts a mirrored
                // sun in every wedge, so unlike the faint luminance ripple this
                // replaced, the sky carries genuinely high-contrast detail across the
                // whole periphery -- which is the worst place in the scene to move
                // anything quickly. The glass surfaces keep the full rate: they are
                // small, central, and the user is looking at them deliberately.
                WvrKaleido k = KaleidoSplit(dir, _WVRKaleidoSegments,
                                            _Time.y * _WVRKaleidoSpin * 0.25h);

                // Sample the gradient along the FOLDED direction rather than the
                // view direction. The fold leaves y alone, so at full strength the
                // vertical gradient is identical to the unfolded sky and only the
                // sun term moves -- which puts one mirrored sun in every wedge, a
                // ring of them around the horizon, for the cost of zero extra
                // pow(). That ring is the effect: mirrored content is what the old
                // luminance-only ripple could never produce.
                //
                // normalize() keeps a partial-strength dial well-formed -- lerping
                // between two azimuths shortens the horizontal component, and
                // without renormalising, a mid-dial value would quietly flatten the
                // sun glow instead of fading the fold.
                float3 sdir = normalize(lerp(dir, k.dir, _WVRKaleidoAmount));
                half3 col = StudioSkyColour(sdir, _Zenith.rgb, _Horizon.rgb, _Nadir.rgb,
                                             _HorizonSharpness, _SunColor.rgb, _SunGlow,
                                             _SunSharp, sun);

                // Everything below is accent, and all of it scales with the shared
                // amount (so PedestalKaleidoscope = 0 really does return a plain
                // .xyz gradient sky -- the sky ignored that lever entirely before)
                // and with radial, which keeps it out of the zenith and nadir where
                // the wedges converge and would otherwise pinch into a hot point.
                half accent = _WVRKaleidoAmount * k.radial;

                // Broad, smooth facet body shading. The elevation term stops
                // neighbouring wedges reading as flat vertical stripes.
                half facet = 0.5h + 0.5h * cos(k.wedge * 3.1415926h + dir.y * 4.2h);
                facet = smoothstep(0.12h, 0.88h, facet);
                col += _PrismColor.rgb * (facet - 0.45h) * _PrismStrength * accent;

                // Dispersion across the wedge, via the same helper the floor and
                // the plinth use, so all three split light in step.
                col = KaleidoDisperse(col, k.wedge, _PrismStrength * 0.5h * accent);

                // Mirror seams plus a faint latitude rhythm: the glass edges of the
                // kaleidoscope barrel. Deliberately the faintest thing here, since
                // they are the only high-contrast lines in peripheral vision.
                half latitude = 1.0h - smoothstep(0.0h, 0.055h,
                    abs(frac((dir.y + 1.0h) * 2.25h) - 0.5h));
                col += _PrismColor.rgb * max(k.seam * 0.7h, latitude * 0.25h)
                       * _LatticeStrength * accent;

                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}

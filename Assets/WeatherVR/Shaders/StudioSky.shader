// Calm kaleidoscope environment for the tabletop exhibit.
//
// The pattern is analytic and deliberately low-frequency: broad mirrored facets
// give the surround a prismatic identity without creating fast, high-contrast
// detail in peripheral vision. Both hemispheres use the same atmospheric blend,
// so there is no hard horizon or dark "floor" under the table.
Shader "WeatherVR/StudioSky"
{
    Properties
    {
        _Zenith     ("Zenith",  Color) = (0.12, 0.22, 0.34, 1)
        _Horizon    ("Horizon", Color) = (0.30, 0.42, 0.52, 1)
        _Nadir      ("Nadir",   Color) = (0.10, 0.17, 0.24, 1)
        _HorizonSharpness ("Vertical Blend", Range(0.5, 6)) = 1.45

        _SunColor   ("Sun Glow Color", Color) = (0.72, 0.78, 0.74, 1)
        _SunGlow    ("Sun Glow Strength", Range(0, 4)) = 0.75
        _SunSharp   ("Sun Glow Sharpness", Range(1, 200)) = 12
        _SunDir     ("Sun Direction", Vector) = (0.3, 0.15, -0.9, 0)

        _PrismColor    ("Prism Accent", Color) = (0.30, 0.68, 0.66, 1)
        _PrismStrength ("Prism Strength", Range(0, 1)) = 0.15
        _LatticeStrength ("Lattice Strength", Range(0, 1)) = 0.045
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
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            half4 _Zenith, _Horizon, _Nadir, _SunColor, _PrismColor;
            half _HorizonSharpness, _SunGlow, _SunSharp;
            half _PrismStrength, _LatticeStrength;
            float4 _SunDir;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);

                // Continuous two-hemisphere gradient. The lower sky remains visible
                // atmosphere rather than collapsing into a floor-like dark band.
                half up = pow(saturate(dir.y), 1.0 / _HorizonSharpness);
                half down = pow(saturate(-dir.y), 1.0 / _HorizonSharpness);
                half3 col = _Horizon.rgb;
                col = lerp(col, _Zenith.rgb, up);
                col = lerp(col, _Nadir.rgb, down);

                // Mirror the azimuth into six broad wedges, then add only a soft
                // luminance variation. This reads as a kaleidoscope in motion while
                // avoiding sharp geometry in peripheral vision.
                const half Tau = 6.2831853h;
                half angle = atan2(dir.z, dir.x);
                half wedge = Tau / 12.0h;
                half folded = abs(frac((angle + wedge * 0.5h) / wedge) - 0.5h) * 2.0h;
                half radial = sqrt(saturate(1.0h - dir.y * dir.y));
                half facet = 0.5h + 0.5h * cos(folded * 3.1415926h + dir.y * 4.2h);
                facet = smoothstep(0.12h, 0.88h, facet);
                col = lerp(col, col + _PrismColor.rgb * (facet - 0.45h),
                           _PrismStrength * radial);

                // Fine lattice is intentionally very faint and wide. It borrows the
                // rhythm of a geometric screen without drawing literal ornament.
                half seam = 1.0h - smoothstep(0.0h, 0.075h, min(folded, 1.0h - folded));
                half latitude = 1.0h - smoothstep(0.0h, 0.055h,
                    abs(frac((dir.y + 1.0h) * 2.25h) - 0.5h));
                col += _PrismColor.rgb * max(seam * 0.7h, latitude * 0.25h)
                       * _LatticeStrength * radial;

                // Broad atmospheric glow rather than a hard bright hotspot.
                float3 sun = normalize(_SunDir.xyz);
                half glow = pow(saturate(dot(dir, sun)), _SunSharp);
                col += _SunColor.rgb * _SunGlow * glow;

                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}

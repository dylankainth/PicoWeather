// Frosted-glass ground plane for the tabletop exhibit's surround.
//
// A soft radial disc: brightest and most opaque under the map, fading to fully
// transparent at the rim so it has no hard edge, with faint concentric rings for
// a glass-pane feel. Colours are set per weather scene so the floor agrees with
// the carousel's glass UI and the sky gradient.
Shader "WeatherVR/GlassEnvironment"
{
    Properties
    {
        _ColorA ("Inner", Color) = (0.40, 0.62, 0.90, 0.45)
        _ColorB ("Outer", Color) = (0.08, 0.16, 0.32, 0.0)
        _RimColor ("Ring Glow", Color) = (0.70, 0.88, 1.0, 0.5)
        _Rings ("Ring Count", Float) = 7
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            half4 _ColorA, _ColorB, _RimColor;
            half _Rings;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // uv is [0,1] with the disc centre at 0.5; map to a signed radius 0..1.
                float2 c = i.uv * 2.0 - 1.0;
                float r = saturate(length(c));

                half3 col = lerp(_ColorA.rgb, _ColorB.rgb, r);
                half a = lerp(_ColorA.a, _ColorB.a, smoothstep(0.0, 1.0, r));

                // No hard edge: fade the whole thing out over the outer quarter.
                a *= 1.0 - smoothstep(0.72, 1.0, r);

                // Faint concentric rings, strongest toward the middle, for a glass feel.
                half ring = abs(frac(r * _Rings) - 0.5) * 2.0;
                half rim = smoothstep(0.82, 1.0, ring) * (1.0 - r);
                col += _RimColor.rgb * rim * _RimColor.a * 0.5;
                a += rim * _RimColor.a * 0.25;

                return fixed4(col, saturate(a));
            }
            ENDCG
        }
    }
    Fallback Off
}

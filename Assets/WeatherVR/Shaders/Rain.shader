// Falling-rain particle streak.
//
// Procedurally soft rather than a textured sprite -- same reasoning as
// Buildings.shader's procedural facade: a few thousand small live streaks share
// one material and zero texture memory. The stretched billboard's own UV gives a
// teardrop shape for free: samples near the leading (v=1) end are widened and
// brightened into a rounded head, samples toward the trailing (v=0) end taper and
// fade, and the whole thing falls off softly across u so the streak has no hard
// rectangular edge. This is what turns a flat-alpha quad into something that reads
// as a droplet instead of a blocky bar.
Shader "WeatherVR/Rain"
{
    Properties
    {
        _TintColor ("Tint", Color) = (0.75, 0.85, 1.0, 1.0)
        _HeadSoftness ("Head Softness", Range(0.05, 1)) = 0.35
        _TailSoftness ("Tail Softness", Range(0.05, 1)) = 0.55
        _EdgeSoftness ("Edge Softness", Range(0.05, 1)) = 0.5
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        Lighting Off
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv    : TEXCOORD0;
            };

            fixed4 _TintColor;
            half _HeadSoftness, _TailSoftness, _EdgeSoftness;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _TintColor;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Stretched-billboard UV: u across the streak's width, v along its
                // length (0 = trailing tail, 1 = leading head -- Unity stretches the
                // quad along velocity with the head at v=1).
                half acrossCenter = abs(i.uv.x - 0.5) * 2.0;      // 0 at centreline, 1 at edge
                half edgeMask = 1.0 - smoothstep(1.0 - _EdgeSoftness, 1.0, acrossCenter);

                // Rounded head: brighten and widen tolerance near v=1.
                half headMask = smoothstep(1.0 - _HeadSoftness, 1.0, i.uv.y);
                // Soft fade into the tail near v=0, so the streak has no hard cut.
                half tailMask = smoothstep(0.0, _TailSoftness, i.uv.y);

                half alpha = edgeMask * tailMask;
                half brighten = 1.0 + headMask * 0.6;

                fixed4 col = i.color;
                col.rgb *= brighten;
                col.a *= alpha;
                return col;
            }
            ENDCG
        }
    }

    Fallback Off
}

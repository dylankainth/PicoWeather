// Emissive channel shader for lightning bolts.
//
// Additive, unlit and depth-testing but not depth-writing, so several ribbons of
// the same bolt overlap into a bright core without z-fighting. UV.x runs across
// the ribbon (the soft-edged glow), UV.y runs down the channel (the taper and
// the travelling brightness of the return stroke).
//
// _Intensity is driven per-bolt through a MaterialPropertyBlock, so every bolt
// shares one material and one draw-call setup.

Shader "WeatherVR/LightningBolt"
{
    Properties
    {
        _BoltColor  ("Bolt Color", Color) = (0.82, 0.88, 1.0, 1)
        _CoreColor  ("Core Color", Color) = (1, 1, 1, 1)
        _Intensity  ("Intensity", Range(0, 4)) = 1
        _CoreWidth  ("Core Width", Range(0.01, 1)) = 0.22
        _GlowFalloff("Glow Falloff", Range(0.5, 8)) = 2.4
        _TipFade    ("Tip Fade", Range(0, 1)) = 0.35
        _Flicker    ("Flicker", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            // After the clouds (2900) so bolts read as being lit through the volume,
            // but before the default Transparent queue so they never draw over the
            // world-space UI. Clouds do not write depth, so this ordering is what
            // actually decides it.
            "Queue"           = "Transparent-50"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Blend One One          // additive: light adds, never occludes
            ZWrite Off
            ZTest LEqual
            Cull Off               // ribbons are viewed from both sides

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _BoltColor)
                UNITY_DEFINE_INSTANCED_PROP(float,  _Intensity)
            UNITY_INSTANCING_BUFFER_END(Props)

            fixed4 _CoreColor;
            half   _CoreWidth;
            half   _GlowFalloff;
            half   _TipFade;
            half   _Flicker;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);

                fixed4 boltColor = UNITY_ACCESS_INSTANCED_PROP(Props, _BoltColor);
                float  intensity = UNITY_ACCESS_INSTANCED_PROP(Props, _Intensity);

                // Distance from the ribbon centreline, 0 at the core, 1 at the edge.
                half d = abs(i.uv.x * 2.0 - 1.0);

                // Saturated white core with a coloured glow around it: this is why
                // lightning photographs as white in the middle and blue-violet outside.
                half core = 1.0 - smoothstep(0.0, _CoreWidth, d);
                half glow = pow(saturate(1.0 - d), _GlowFalloff);

                // Fade towards the tip of the channel and its branches.
                half taper = 1.0 - _TipFade * i.uv.y;

                // Sub-frame flicker along the channel: the return stroke is not
                // uniformly bright along its length.
                half flicker = 1.0 - _Flicker * (0.5 + 0.5 * sin(i.uv.y * 34.0 + _Time.y * 90.0));

                half3 color = _CoreColor.rgb * core + boltColor.rgb * glow;
                half  amount = intensity * taper * flicker;

                // Additive: alpha is ignored by the blend, but keep it meaningful.
                return fixed4(color * amount, saturate(amount));
            }
            ENDCG
        }
    }

    FallBack Off
}

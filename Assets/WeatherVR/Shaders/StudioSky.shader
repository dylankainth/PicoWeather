// Studio environment skybox.
//
// A dark, graded gradient rather than a literal daytime sky: the app is a
// tabletop exhibit, and a product shot of an exhibit wants a controlled studio
// backdrop, not a bright blue sky it appears to float in. A faint warm glow is
// placed in the sun's direction so the clouds have something to be backlit
// against, which is most of what sells them as volumetric.
Shader "WeatherVR/StudioSky"
{
    Properties
    {
        _Zenith     ("Zenith",  Color) = (0.015, 0.018, 0.030, 1)
        _Horizon    ("Horizon", Color) = (0.050, 0.065, 0.095, 1)
        _Nadir      ("Nadir",   Color) = (0.008, 0.009, 0.013, 1)
        _HorizonSharpness ("Horizon Sharpness", Range(0.5, 6)) = 2.2

        _SunColor   ("Sun Glow Color", Color) = (0.55, 0.42, 0.30, 1)
        _SunGlow    ("Sun Glow Strength", Range(0, 4)) = 1.4
        _SunSharp   ("Sun Glow Sharpness", Range(1, 200)) = 18
        _SunDir     ("Sun Direction", Vector) = (0.3, 0.15, -0.9, 0)
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

            half4 _Zenith, _Horizon, _Nadir, _SunColor;
            half _HorizonSharpness, _SunGlow, _SunSharp;
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

                // Vertical gradient. The horizon band is narrowed by the sharpness
                // exponent so the studio "floor" reads as a distinct dark base.
                half up = pow(saturate(dir.y), 1.0 / _HorizonSharpness);
                half down = pow(saturate(-dir.y), 1.0 / _HorizonSharpness);
                half3 col = _Horizon.rgb;
                col = lerp(col, _Zenith.rgb, up);
                col = lerp(col, _Nadir.rgb, down);

                // Atmospheric glow toward the sun, only in the upper hemisphere so the
                // floor stays clean.
                float3 sun = normalize(_SunDir.xyz);
                half glow = pow(saturate(dot(dir, sun)), _SunSharp) * saturate(dir.y + 0.15);
                col += _SunColor.rgb * _SunGlow * glow;

                return fixed4(col, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}

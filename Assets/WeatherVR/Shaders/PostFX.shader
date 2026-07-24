// Offline post-processing stack for the hero render.
//
// This is where most of the "photorealistic" impression actually comes from:
// physically-lit HDR clouds still look like a tech demo until they are passed
// through bloom (which gives the sunlit edges and the lightning their glow),
// an ACES filmic tonemap (which rolls off the highlights the way a camera
// does instead of clipping them to flat white), and a vignette (which frames
// the subject). Four passes, driven by CaptureTool.
Shader "WeatherVR/PostFX"
{
    Properties { _MainTex ("Texture", 2D) = "black" {} }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _BloomTex;

    float2 _BlurDir;
    half  _Threshold;
    half  _Knee;
    half  _BloomIntensity;
    half  _Exposure;
    half  _Vignette;
    half  _Saturation;
    half4 _Lift;

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

    v2f vert(appdata_img v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    // Karis average weight: dims fireflies so bloom does not sparkle.
    half3 Prefilter(half3 c)
    {
        half brightness = max(c.r, max(c.g, c.b));
        half soft = brightness - _Threshold + _Knee;
        soft = clamp(soft, 0, 2 * _Knee);
        soft = soft * soft / (4 * _Knee + 1e-4);
        half contribution = max(soft, brightness - _Threshold) / max(brightness, 1e-4);
        return c * contribution;
    }

    // Narkowicz ACES filmic approximation.
    half3 ACES(half3 x)
    {
        const half a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
        return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0 — bright-pass prefilter
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(v2f i) : SV_Target
            {
                half3 c = tex2D(_MainTex, i.uv).rgb;
                return half4(Prefilter(c), 1);
            }
            ENDCG
        }

        // 1 — separable 9-tap gaussian blur (direction from _BlurDir)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(v2f i) : SV_Target
            {
                float2 step = _BlurDir * _MainTex_TexelSize.xy;
                half3 sum = tex2D(_MainTex, i.uv).rgb * 0.227027;
                sum += tex2D(_MainTex, i.uv + step * 1.3846).rgb * 0.316216;
                sum += tex2D(_MainTex, i.uv - step * 1.3846).rgb * 0.316216;
                sum += tex2D(_MainTex, i.uv + step * 3.2308).rgb * 0.070270;
                sum += tex2D(_MainTex, i.uv - step * 3.2308).rgb * 0.070270;
                return half4(sum, 1);
            }
            ENDCG
        }

        // 2 — composite: base + bloom, exposure, ACES, grade, vignette
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            half4 frag(v2f i) : SV_Target
            {
                half3 c = tex2D(_MainTex, i.uv).rgb;
                half3 bloom = tex2D(_BloomTex, i.uv).rgb;
                c += bloom * _BloomIntensity;

                c *= _Exposure;
                c = ACES(c);

                // Lift shadows a touch toward cool, and desaturate/​saturate.
                half luma = dot(c, half3(0.2126, 0.7152, 0.0722));
                c = lerp(half3(luma, luma, luma), c, _Saturation);
                c += _Lift.rgb * (1.0 - c);

                // Vignette.
                float2 d = i.uv - 0.5;
                half vig = smoothstep(0.8, 0.2, dot(d, d) * _Vignette);
                c *= lerp(1.0, vig, saturate(_Vignette));

                // Encode to sRGB for the PNG. Done explicitly, and the capture writes
                // into linear render targets, so the result does not depend on whether
                // the project's colour space would otherwise apply a hardware
                // conversion — which is what blew the first render out to white.
                c = pow(saturate(c), 1.0 / 2.2);
                return half4(c, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}

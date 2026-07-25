Shader "WeatherVR/Water"
{
    // A flat, translucent storm-surge plane. Deliberately hand-written rather than
    // a surface shader (see TerrainSurface.shader / Buildings.shader) so the
    // Transparent queue, single pass and lack of shadow sampling are all explicit —
    // this plane only ever needs to be cheap and to sit correctly behind whatever
    // terrain or building geometry pokes above the water line.
    //
    // ZWrite is deliberately Off: opaque terrain/buildings (Geometry queue) already
    // sit in the depth buffer by the time this Transparent-queue pass runs, so a
    // building or hill above the water surface still occludes it correctly via
    // ZTest alone. Not writing depth just means this plane cannot occlude a second
    // transparent object behind it, which we don't have.
    Properties
    {
        _Color ("Water Colour", Color) = (0.18, 0.42, 0.62, 0.55)
        _ShoreColor ("Shoreline Tint", Color) = (0.55, 0.75, 0.80, 0.65)
        _RippleScale ("Ripple Scale", Float) = 18.0
        _RippleSpeed ("Ripple Speed", Float) = 0.35
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 2.0
            #include "UnityCG.cginc"

            fixed4 _Color;
            fixed4 _ShoreColor;
            float _RippleScale;
            float _RippleSpeed;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float3 worldPosition : TEXCOORD0;
                half3 worldNormal : TEXCOORD1;
                float2 uv : TEXCOORD2;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.position = UnityObjectToClipPos(input.vertex);
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 normal = normalize(input.worldNormal);
                half3 viewDirection =
                    normalize(_WorldSpaceCameraPos.xyz - input.worldPosition);
                half3 keyDirection = normalize(half3(-0.42h, 0.58h, -0.70h));

                // Two offset sine waves give a cheap, non-repeating shimmer without a
                // normal map — at tabletop scale (a few cm across) this reads fine at
                // arm's length and costs nothing beyond two sin() calls.
                float t = _Time.y * _RippleSpeed;
                float ripple = sin((input.uv.x + input.uv.y) * _RippleScale + t)
                             + sin((input.uv.x - input.uv.y) * _RippleScale * 1.3 - t * 1.7);
                ripple = ripple * 0.5 + 0.5;

                half diffuse = saturate(dot(normal, keyDirection)) * 0.5h + 0.55h;
                half rim = pow(1.0h - saturate(dot(normal, viewDirection)), 4.0h);

                fixed3 baseColour = lerp(_Color.rgb, _ShoreColor.rgb, ripple * 0.25) * input.color.rgb;
                fixed3 colour = baseColour * diffuse;
                colour += _ShoreColor.rgb * rim * 0.35;
                colour += ripple * 0.03; // faint sparkle

                fixed alpha = saturate(_Color.a + rim * 0.25);
                return fixed4(colour, alpha * input.color.a);
            }
            ENDCG
        }
    }

    Fallback Off
}

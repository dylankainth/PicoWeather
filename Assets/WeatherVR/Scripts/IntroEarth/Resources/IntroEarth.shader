Shader "WeatherVR/IntroEarth"
{
    Properties
    {
        _Color ("Colour", Color) = (0.1, 0.4, 0.7, 1)
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
            ZWrite On
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            fixed4 _Color;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float3 worldPosition : TEXCOORD0;
                half3 worldNormal : TEXCOORD1;
                fixed4 color : COLOR;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.color = input.color;
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                half3 normal = normalize(input.worldNormal);
                half3 viewDirection =
                    normalize(_WorldSpaceCameraPos.xyz - input.worldPosition);
                half3 keyDirection = normalize(half3(-0.42h, 0.58h, -0.70h));

                half diffuse = saturate(dot(normal, keyDirection)) * 0.46h + 0.58h;
                half rim = pow(1.0h - saturate(dot(normal, viewDirection)), 3.0h);

                fixed3 baseColour = _Color.rgb * input.color.rgb;
                fixed3 colour = baseColour * diffuse;
                colour += baseColour * rim * 0.24h;
                return fixed4(colour, _Color.a * input.color.a);
            }
            ENDCG
        }
    }

    Fallback Off
}

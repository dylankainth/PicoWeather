// Holographic plinth the tabletop map stands on.
//
// A dark glassy body lit by the scene's one directional light and its flat
// ambient, with a view-dependent fresnel rim and a vertex-painted glow band
// layered on top in the flared lip just under the map's edge. The glow band
// is baked into vertex colour by PedestalMeshBuilder (full strength on the
// lip, fading to zero toward the base) rather than computed from geometry
// here, so the shader stays a flat-shaded, single-pass, mobile-cheap prop --
// no textures, no surface-shader lighting model overhead.
Shader "WeatherVR/Pedestal"
{
    Properties
    {
        [Header(Body)]
        _BaseColor   ("Base Color", Color) = (0.035, 0.05, 0.085, 1)
        _AmbientMix  ("Ambient Contribution", Range(0, 1)) = 0.8

        [Header(Rim Glow)]
        _RimColor    ("Rim Glow Color", Color) = (0.36, 0.72, 1.0, 1)
        _RimPower    ("Fresnel Power", Range(0.5, 8)) = 3.0
        _RimStrength ("Fresnel Rim Strength", Range(0, 4)) = 1.1

        [Header(Lip Band)]
        _BandStrength ("Vertex Glow Band Strength", Range(0, 4)) = 1.4
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float3 worldNrm : TEXCOORD1;
                fixed4 color    : COLOR;
            };

            fixed4 _BaseColor;
            half   _AmbientMix;
            fixed4 _RimColor;
            half   _RimPower;
            half   _RimStrength;
            half   _BandStrength;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNrm = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 normal = normalize(i.worldNrm);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);

                // Flat diffuse from the scene's single directional light, blended
                // with the flat ambient the app already sets via RenderSettings.
                half ndotl = saturate(dot(normal, lightDir));
                fixed3 lit = _BaseColor.rgb * (unity_AmbientSky.rgb * _AmbientMix
                                              + _LightColor0.rgb * ndotl);

                // Fresnel rim: brightest at grazing angles, so the plinth's silhouette
                // reads as an edge-lit glass slab from any viewing angle.
                half fresnel = pow(1.0 - saturate(dot(normal, viewDir)), _RimPower);
                fixed3 rim = _RimColor.rgb * fresnel * _RimStrength;

                // Vertex-painted glow band (r channel, 1 on the lip, 0 at the base) --
                // an emissive accent independent of view angle, giving the lip a
                // constant glow rather than one that only shows up at grazing angles.
                fixed3 band = _RimColor.rgb * i.color.r * _BandStrength;

                return fixed4(lit + rim + band, 1);
            }
            ENDCG
        }
    }

    Fallback "Mobile/Diffuse"
}

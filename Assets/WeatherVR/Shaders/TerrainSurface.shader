// Basemap shader for the terrain mesh.
//
// Built-in RP surface shader rather than a hand-written forward pass: it gets us
// correct lighting from the sun directional light *and* from the transient
// lightning point lights for free, including the additive pass, which is exactly
// the "scene momentarily illuminated by the strike" behaviour the spec asks for.
//
// Vertex colour carries terrain semantics from TerrainMeshBuilder:
//   r = water mask, g = normalised elevation, b = slope, a = shoreline proximity.

Shader "WeatherVR/TerrainSurface"
{
    Properties
    {
        _MainTex          ("Satellite Basemap", 2D) = "white" {}
        _Saturation       ("Saturation", Range(0, 2)) = 1.05
        _Brightness       ("Brightness", Range(0, 2)) = 1.0

        [Header(Water)]
        _WaterTint        ("Water Tint", Color) = (0.16, 0.30, 0.42, 1)
        _WaterSmoothness  ("Water Smoothness", Range(0, 1)) = 0.85
        _WaterSpecular    ("Water Specular", Range(0, 1)) = 0.55
        _LandSmoothness   ("Land Smoothness", Range(0, 1)) = 0.08

        [Header(Relief)]
        _ReliefStrength   ("Slope Shading", Range(0, 1)) = 0.35
        _ShoreFoam        ("Shoreline Lightening", Range(0, 1)) = 0.18

        [Header(Edges)]
        _EdgeColor        ("Map Edge Color", Color) = (0.05, 0.07, 0.10, 1)
        _EdgeWidth        ("Map Edge Width", Range(0, 0.1)) = 0.012
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200

        CGPROGRAM
        // Standard lighting so water reads as water. `addshadow` is deliberately
        // omitted: the map is 2 m across and shadow casting from it costs more than
        // it returns on a mobile GPU.
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0
        #pragma multi_compile_instancing

        sampler2D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
            float4 color : COLOR;
        };

        half  _Saturation;
        half  _Brightness;
        fixed4 _WaterTint;
        half  _WaterSmoothness;
        half  _WaterSpecular;
        half  _LandSmoothness;
        half  _ReliefStrength;
        half  _ShoreFoam;
        fixed4 _EdgeColor;
        half  _EdgeWidth;

        UNITY_INSTANCING_BUFFER_START(Props)
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 base = tex2D(_MainTex, IN.uv_MainTex);

            half water    = IN.color.r;
            half slope    = IN.color.b;
            half shore    = IN.color.a;

            // Grade the imagery a little: satellite composites are flat by design,
            // and at tabletop scale a touch of contrast helps the eye read terrain.
            half luma = dot(base.rgb, half3(0.299, 0.587, 0.114));
            base.rgb = lerp(half3(luma, luma, luma), base.rgb, _Saturation) * _Brightness;

            // Slope shading. Real relief here is tiny, so this is what actually
            // communicates the hills rather than the geometry doing it alone.
            base.rgb *= 1.0 - slope * _ReliefStrength;

            // Shoreline lightening: shallow, sediment-laden, and brighter in every
            // real image of this coast.
            base.rgb += _ShoreFoam * shore * (1.0 - water) * half3(0.12, 0.12, 0.10);

            base.rgb = lerp(base.rgb, base.rgb * _WaterTint.rgb * 2.0, water * 0.65);

            // Darken the outermost band so the map reads as a discrete object sitting
            // on the table rather than as a texture that has been pasted onto it.
            float2 d = abs(IN.uv_MainTex - 0.5);
            float edge = saturate((max(d.x, d.y) - (0.5 - _EdgeWidth)) / max(_EdgeWidth, 1e-4));
            base.rgb = lerp(base.rgb, _EdgeColor.rgb, edge);

            o.Albedo     = base.rgb;
            o.Smoothness = lerp(_LandSmoothness, _WaterSmoothness, water);
            o.Metallic   = water * _WaterSpecular * 0.35;
            o.Alpha      = 1.0;
        }
        ENDCG
    }

    FallBack "Mobile/Diffuse"
}

// Facade shader for the combined buildings mesh.
//
// No texture: the "windows" are a cheap procedural band per floor, derived
// entirely from BuildingMeshBuilder's own vertex UV convention (u = the
// building's true height in metres, v = height in floors from the base) rather
// than from a tiling texture, so a bake with hundreds of buildings costs one
// shared material and zero texture memory.

Shader "WeatherVR/Buildings"
{
    Properties
    {
        [Header(Facade)]
        _BaseSmoothness   ("Base Smoothness", Range(0, 1)) = 0.12

        [Header(Windows)]
        _WindowColor      ("Window Color", Color) = (0.80, 0.86, 0.95, 1)
        _WindowSmoothness ("Window Smoothness", Range(0, 1)) = 0.65
        _WindowMetallic   ("Window Metallic", Range(0, 1)) = 0.2
        _WindowBand       ("Window Band Height", Range(0, 1)) = 0.62
        _SillBand         ("Sill Height", Range(0, 1)) = 0.18

        [Header(Roof)]
        _RoofDarken       ("Roof Darkening", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 200

        CGPROGRAM
        // No addshadow: same reasoning as TerrainSurface -- the map is 2 m across,
        // and shadow-casting from it costs a full extra pass for no visible gain.
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        #pragma multi_compile_instancing

        struct Input
        {
            float2 floorUv;
            float3 vertexTint;
            float3 worldNormal;
            INTERNAL_DATA
        };

        half   _BaseSmoothness;
        fixed4 _WindowColor;
        half   _WindowSmoothness;
        half   _WindowMetallic;
        half   _WindowBand;
        half   _SillBand;
        half   _RoofDarken;

        UNITY_INSTANCING_BUFFER_START(Props)
        UNITY_INSTANCING_BUFFER_END(Props)

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.floorUv = v.texcoord.xy;
            o.vertexTint = v.color.rgb;
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // floorUv.y was baked as height-in-floors, so a bare frac() already
            // cycles once per storey with no further scaling needed here.
            float floorFrac = frac(IN.floorUv.y);
            float window = smoothstep(_SillBand, _SillBand + 0.05, floorFrac) *
                           (1.0 - smoothstep(_WindowBand, _WindowBand + 0.05, floorFrac));

            fixed3 albedo = lerp(IN.vertexTint, _WindowColor.rgb, window);

            // Roof faces (+Y normal) read flat and a touch darker, so a bird's-eye
            // view still tells rooftops from walls without a second material.
            half upFacing = saturate(dot(normalize(IN.worldNormal), half3(0, 1, 0)));
            albedo *= lerp(1.0, 1.0 - _RoofDarken, upFacing);

            o.Albedo     = albedo;
            o.Smoothness = lerp(_BaseSmoothness, _WindowSmoothness, window * (1.0 - upFacing));
            o.Metallic   = window * _WindowMetallic * (1.0 - upFacing);
            o.Alpha      = 1.0;
        }
        ENDCG
    }

    FallBack "Mobile/Diffuse"
}

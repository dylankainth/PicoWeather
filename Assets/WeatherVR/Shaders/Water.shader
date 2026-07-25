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
        // _Color is legacy: it was the whole water colour before depth shading was
        // added below. FloodRenderer still sets it (WaterColor) for anything that reads
        // it directly, but this shader's frag no longer samples it — _ShallowColor/
        // _DeepColor now do that job, varying with depth instead of being flat.
        _Color ("Water Colour (legacy, unused below)", Color) = (0.18, 0.42, 0.62, 0.55)
        _ShoreColor ("Shoreline Tint", Color) = (0.55, 0.75, 0.80, 0.65)
        _RippleScale ("Ripple Scale", Float) = 18.0
        _RippleSpeed ("Ripple Speed", Float) = 0.35

        // Depth-aware shading, added for the storm-surge overlay. _HeightTex is a
        // single-channel bake of the terrain heightfield in the same normalised (u,v)
        // this quad's UVs already use (see FloodRenderer.EnsureHeightTexture), so no
        // vertex-side plumbing was needed to add this.
        _HeightTex ("Terrain Height (R, normalised)", 2D) = "white" {}
        _MinElevation ("Min Elevation (m asl)", Float) = 0.0
        _ElevationRange ("Elevation Range (m)", Float) = 1.0
        _LevelMeters ("Water Level (m asl)", Float) = 0.0

        // Hydraulic connectivity, added alongside the depth shading above. _ConnectTex
        // bakes FloodConnectivityField -- the minimum water level at which each cell is
        // reachable from the river through real Thames defence crest heights (see
        // FloodRenderer.EnsureConnectivityTexture). A cell only floods once the water
        // level reaches its connect level, which is always >= its own ground elevation
        // by construction, so this is a strictly tighter gate than the terrain-height
        // clip alone -- a hollow behind a defence that the ground-height clip would
        // happily fill stays dry until the water actually reaches the level needed to
        // get there.
        _ConnectTex ("Connectivity Level (R, normalised)", 2D) = "white" {}
        _ConnectMin ("Connectivity Min (m asl)", Float) = 0.0
        _ConnectRange ("Connectivity Range (m)", Float) = 0.0
        _ShallowColor ("Shallow Water Colour", Color) = (0.55, 0.78, 0.80, 0.30)
        _DeepColor ("Deep Water Colour", Color) = (0.05, 0.16, 0.28, 0.80)
        _DeepDepth ("Deep Water Depth Threshold (m)", Float) = 10.0
        _FoamDepth ("Foam Band Depth (m)", Float) = 1.0
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

            sampler2D _HeightTex;
            float _MinElevation;
            float _ElevationRange;
            float _LevelMeters;
            fixed4 _ShallowColor;
            fixed4 _DeepColor;
            float _DeepDepth;
            float _FoamDepth;

            sampler2D _ConnectTex;
            float _ConnectMin;
            float _ConnectRange;

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

                // Depth-aware shading: sample the baked terrain height at this pixel's
                // (u,v) — the same normalised square the mesh's own UVs cover — and
                // compare it to the current water level. clip() drops every pixel where
                // the ground is above the water, so the drawn waterline follows real
                // valleys and ridges instead of the mesh's flat rectangular edge, and it
                // reduces shaded pixels above the line rather than adding a second
                // blended layer over them.
                float ground = _MinElevation + tex2D(_HeightTex, input.uv).r * _ElevationRange;
                float depth = _LevelMeters - ground;

                // Hydraulic connectivity: this cell's minimum water level to be reachable
                // from the river at all, per real defence crest heights. Always >= ground
                // by construction, so this clip alone is normally the tighter one — kept
                // as a separate term (rather than folded into `depth`) so a hollow behind
                // an intact defence stays dry even though its ground is below the water
                // plane, which is exactly the case the old bathtub model got wrong.
                float connectLevel = _ConnectMin + tex2D(_ConnectTex, input.uv).r * _ConnectRange;
                float connected = _LevelMeters - connectLevel;
                clip(min(depth, connected));

                float d01 = saturate(depth / max(_DeepDepth, 0.0001));
                float foam = smoothstep(_FoamDepth, 0.0, depth);

                half3 normal = normalize(input.worldNormal);
                half3 viewDirection =
                    normalize(_WorldSpaceCameraPos.xyz - input.worldPosition);
                half3 keyDirection = normalize(half3(-0.42h, 0.58h, -0.70h));

                // Two offset sine waves give a cheap, non-repeating shimmer without a
                // normal map — at tabletop scale (a few cm across) this reads fine at
                // arm's length and costs nothing beyond two sin() calls. Shallow water
                // is visibly more agitated than deep water.
                float t = _Time.y * _RippleSpeed;
                float ripple = sin((input.uv.x + input.uv.y) * _RippleScale + t)
                             + sin((input.uv.x - input.uv.y) * _RippleScale * 1.3 - t * 1.7);
                ripple = (ripple * 0.5 + 0.5) * (1.0 - 0.6 * d01);

                half diffuse = saturate(dot(normal, keyDirection)) * 0.5h + 0.55h;
                half rim = pow(1.0h - saturate(dot(normal, viewDirection)), 4.0h);

                // _ShallowColor/_DeepColor (not _Color — see the Properties block) are
                // the primary read: shallow, pale water over submerged low ground,
                // grading dark toward the deepest flooded point, with a foam band right
                // at the true shoreline.
                fixed4 depthColour = lerp(_ShallowColor, _DeepColor, d01);
                fixed3 baseColour =
                    lerp(depthColour.rgb, _ShoreColor.rgb, ripple * 0.25 + foam * 0.5) * input.color.rgb;
                fixed3 colour = baseColour * diffuse;
                colour += _ShoreColor.rgb * (rim * 0.35 + foam * 0.4);
                colour += ripple * 0.03; // faint sparkle

                fixed alpha = saturate(depthColour.a + rim * 0.25 + foam * 0.2);
                return fixed4(colour, alpha * input.color.a);
            }
            ENDCG
        }
    }

    Fallback Off
}

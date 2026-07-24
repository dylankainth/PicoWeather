// Volumetric cloud raymarcher for the tabletop weather map.
//
// The clouds are drawn as a single transparent box enclosing the atmosphere
// column above the map. Rays are marched in WORLD space (so Beer-Lambert
// extinction uses real step lengths even though the box has a wildly
// non-uniform scale) while sample positions are transformed to OBJECT space to
// index the density volume.
//
// Density comes from two textures:
//   _DensityVolume  R8 3D, built by CloudVolumeBuilder from the weather grid.
//                   This carries the real meteorology: where the cloud is, and
//                   at what altitude, per the ECMWF low/mid/high layers.
//   _DetailNoise    R8 3D, tiling Worley-fBm. This carries the *look* -- the
//                   billows and wisps that the 31 km-resolution weather model
//                   cannot possibly contain. It scrolls with the wind.
//
// Built-in RP, forward, single-pass-instanced safe. Occlusion against the
// terrain uses _CameraDepthTexture rather than the depth buffer, because the
// box is drawn with ZTest Always so it survives the camera being inside it.

Shader "WeatherVR/VolumetricClouds"
{
    Properties
    {
        [NoScaleOffset] _DensityVolume ("Density Volume (3D)", 3D) = "black" {}
        [NoScaleOffset] _DetailNoise   ("Detail Noise (3D)", 3D) = "white" {}

        [Header(Density)]
        _DensityScale     ("Density Scale", Range(0, 8)) = 2.4
        _DetailStrength   ("Detail Strength", Range(0, 1)) = 0.55
        _DetailScale      ("Detail Tiling", Range(0.5, 16)) = 4.0
        _CoverageBias     ("Coverage Bias", Range(-0.5, 0.5)) = 0.0
        _Absorption       ("Absorption", Range(0, 8)) = 1.7

        [Header(Scattering)]
        _ScatterColor     ("Scatter Tint", Color) = (1, 1, 1, 1)
        _AmbientSky       ("Ambient Sky", Color) = (0.42, 0.52, 0.68, 1)
        _AmbientGround    ("Ambient Ground", Color) = (0.24, 0.24, 0.22, 1)
        _Anisotropy       ("Forward Scattering (g)", Range(-0.95, 0.95)) = 0.45
        _BackScatter      ("Back Scattering (g)", Range(-0.95, 0)) = -0.15
        _SilverIntensity  ("Silver Lining", Range(0, 4)) = 1.6
        _SilverExponent   ("Silver Sharpness", Range(1, 64)) = 12
        _MultiScatter     ("Multi-Scatter Boost", Range(0, 2)) = 0.7
        _PowderStrength   ("Powder (dark edges)", Range(0, 1)) = 0.5
        _SunIntensity     ("Sun Intensity", Range(0, 8)) = 2.6

        [Header(Marching)]
        _StepCount        ("Primary Steps", Range(8, 96)) = 48
        _LightSteps       ("Light Steps", Range(0, 6)) = 3
        _LightStepLength  ("Light Step Length", Range(0.005, 0.4)) = 0.09
        _BlueNoiseOffset  ("Dither Amount", Range(0, 1)) = 1.0
        // 1 clips the march against scene depth (correct on device, where a depth
        // texture exists). 0 skips it — needed for offline captures that render to
        // an isolated target with no populated _CameraDepthTexture.
        _DepthClip        ("Depth Clip", Float) = 1
        // Front by default: the VR camera sits inside the volume, so the back faces
        // are what we raymarch from. An offline camera outside the box also wants
        // Front — but this is exposed so a capture can rule culling in or out.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1
        // Diagnostic: >0 fills the box with uniform fog, ignoring the density volume,
        // to separate a volume-binding problem from a raymarch/render-path problem.
        _DebugFog         ("Debug Fog", Float) = 0
    }

    SubShader
    {
        Tags
        {
            // Ahead of the default Transparent queue (3000) on purpose. This pass
            // draws with ZTest Always and the depth texture it clips against contains
            // only opaque geometry, so nothing stops it covering a transparent object
            // — including the world-space provenance panel, which sits at 3000 and
            // would otherwise be washed out by cloud drawn on top of it. Ordering is
            // clouds (2900), lightning (2950), UI (3000).
            "Queue"            = "Transparent-100"
            "RenderType"       = "Transparent"
            "IgnoreProjector"  = "True"
            "DisableBatching"  = "True"   // we need per-object space to be meaningful
        }

        Pass
        {
            // ForwardBase so the pass receives the directional light through
            // _WorldSpaceLightPos0 / _LightColor0, which the sun scattering depends on.
            Tags { "LightMode" = "ForwardBase" }

            // Premultiplied alpha: the raymarch already weights colour by coverage,
            // so this composites correctly over the terrain without double-darkening.
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull [_Cull]      // Front by default; keeps the volume visible from inside

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            // Hard ceiling on the loop; _StepCount throttles it at runtime so the
            // perf governor can trade quality for frame time without a shader swap.
            #define MAX_STEPS 96
            #define MAX_LIGHT_STEPS 6
            #define MAX_BOLTS 4

            UNITY_DECLARE_TEX3D(_DensityVolume);
            UNITY_DECLARE_TEX3D(_DetailNoise);
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            half  _DensityScale;
            half  _DetailStrength;
            half  _DetailScale;
            half  _CoverageBias;
            half  _Absorption;

            fixed4 _ScatterColor;
            fixed4 _AmbientSky;
            fixed4 _AmbientGround;
            half  _Anisotropy;
            half  _BackScatter;
            half  _SilverIntensity;
            half  _SilverExponent;
            half  _MultiScatter;
            half  _PowderStrength;
            half  _SunIntensity;

            float _StepCount;
            float _LightSteps;
            float _LightStepLength;
            float _BlueNoiseOffset;
            float _DepthClip;
            float _DebugFog;

            // Set from CloudRenderer each frame.
            float3 _WindScroll;        // detail-noise offset, object space
            float  _CloudTime;

            // Lightning in-scattering. Set from LightningDirector.
            int    _BoltCount;
            float4 _BoltPositions[MAX_BOLTS];   // xyz world, w = radius
            float4 _BoltColors[MAX_BOLTS];      // rgb premultiplied by intensity

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos: TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos       = UnityObjectToClipPos(v.vertex);
                o.worldPos  = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // ----------------------------------------------------------- helpers

            // Slab intersection against the unit box [-0.5, 0.5]^3 in object space.
            // rd is the world ray direction pushed through the inverse object matrix
            // WITHOUT renormalising, so the returned t values are world distances.
            bool IntersectUnitBox(float3 ro, float3 rd, out float tNear, out float tFar)
            {
                float3 invD = 1.0 / (rd + 1e-9);
                float3 t0 = (-0.5 - ro) * invD;
                float3 t1 = ( 0.5 - ro) * invD;
                float3 tMin = min(t0, t1);
                float3 tMax = max(t0, t1);

                tNear = max(max(tMin.x, tMin.y), tMin.z);
                tFar  = min(min(tMax.x, tMax.y), tMax.z);
                return tFar > max(tNear, 0.0);
            }

            // Henyey-Greenstein phase function: the reason cloud edges glow when you
            // look towards the sun through them.
            half HenyeyGreenstein(half cosTheta, half g)
            {
                half g2 = g * g;
                half denom = 1.0 + g2 - 2.0 * g * cosTheta;
                return (1.0 - g2) / (4.0 * UNITY_PI * pow(max(denom, 1e-4), 1.5));
            }

            // Interleaved-gradient noise. Dithering the ray start hides the banding
            // that a 48-step march would otherwise show as concentric shells.
            float InterleavedGradientNoise(float2 uv)
            {
                return frac(52.9829189 * frac(dot(uv, float2(0.06711056, 0.00583715))));
            }

            half SampleDensity(float3 objPos)
            {
                // Object space is [-0.5, 0.5]; the volume is indexed in [0, 1].
                float3 uvw = objPos + 0.5;

                // Outside the box contributes nothing. The march is already clipped
                // to the box, but the light march below is not.
                if (any(uvw < 0.0) || any(uvw > 1.0)) return 0.0;

                // Diagnostic fog: a soft slab in the lower-middle of the box, ignoring
                // the density volume entirely.
                if (_DebugFog > 0.0)
                {
                    half h = uvw.y;
                    half slab = smoothstep(0.0, 0.15, h) * (1.0 - smoothstep(0.45, 0.85, h));
                    return _DebugFog * slab * _DensityScale;
                }

                half base = UNITY_SAMPLE_TEX3D_LOD(_DensityVolume, uvw, 0).r;
                base = saturate(base + _CoverageBias);
                if (base <= 0.001) return 0.0;

                // Detail erodes the base shape rather than adding to it, which is what
                // keeps cloud edges wispy instead of merely bumpy.
                float3 detailUvw = uvw * _DetailScale + _WindScroll;
                half detail = UNITY_SAMPLE_TEX3D_LOD(_DetailNoise, frac(detailUvw), 0).r;

                // Erosion strengthens towards the edges of the base shape: dense cores
                // stay solid, thin fringes break up.
                half erosion = _DetailStrength * (1.0 - base);
                half density = saturate((base - erosion * (1.0 - detail)) / max(1.0 - erosion, 1e-3));

                return density * _DensityScale;
            }

            // Beer-Lambert transmittance towards the sun, marched with a handful of
            // long steps. Cheap, and at this scale nobody can tell.
            half LightTransmittance(float3 objPos, float3 lightDirObj)
            {
                int steps = (int)min(_LightSteps, MAX_LIGHT_STEPS);
                if (steps <= 0) return 1.0;

                half accumulated = 0.0;
                float stepLength = _LightStepLength;
                [loop]
                for (int i = 0; i < MAX_LIGHT_STEPS; ++i)
                {
                    if (i >= steps) break;
                    float3 p = objPos + lightDirObj * stepLength * (i + 0.5);
                    accumulated += SampleDensity(p) * stepLength;
                }
                return exp(-accumulated * _Absorption);
            }

            half3 LightningInScatter(float3 worldPos)
            {
                half3 sum = 0;
                [loop]
                for (int i = 0; i < MAX_BOLTS; ++i)
                {
                    if (i >= _BoltCount) break;
                    float3 delta = _BoltPositions[i].xyz - worldPos;
                    float  d2 = dot(delta, delta);
                    float  radius = max(_BoltPositions[i].w, 1e-3);
                    // Inverse-square with a soft core so a bolt inside the volume does
                    // not blow out to infinity.
                    sum += _BoltColors[i].rgb / (1.0 + d2 / (radius * radius));
                }
                return sum;
            }

            // ------------------------------------------------------------- fragment

            fixed4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 roWorld = _WorldSpaceCameraPos;
                float3 rdWorld = normalize(i.worldPos - roWorld);

                float3 roObj = mul(unity_WorldToObject, float4(roWorld, 1.0)).xyz;
                float3 rdObj = mul((float3x3)unity_WorldToObject, rdWorld);

                float tNear, tFar;
                if (!IntersectUnitBox(roObj, rdObj, tNear, tFar)) discard;

                // Start at the camera when it is already inside the volume.
                tNear = max(tNear, 0.0);

                // Clip the march where the opaque scene begins, so clouds do not
                // bleed through the terrain or through the user's controllers. Skipped
                // for offline captures, which render to a target with no depth texture.
                if (_DepthClip > 0.5)
                {
                    float rawDepth = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screenPos));
                    float sceneEyeDepth = LinearEyeDepth(rawDepth);
                    float3 camForward = -UNITY_MATRIX_V._m20_m21_m22;
                    float sceneDist = sceneEyeDepth / max(dot(rdWorld, camForward), 1e-4);
                    tFar = min(tFar, sceneDist);
                }

                if (tFar <= tNear) discard;

                int steps = (int)clamp(_StepCount, 8, MAX_STEPS);
                float stepLength = (tFar - tNear) / steps;

                // Jitter the start by up to one step to trade banding for noise.
                float2 pixel = i.screenPos.xy / max(i.screenPos.w, 1e-4) * _ScreenParams.xy;
                float dither = InterleavedGradientNoise(pixel + frac(_CloudTime) * 64.0);
                float t = tNear + stepLength * dither * _BlueNoiseOffset;

                float3 sunDirWorld = normalize(_WorldSpaceLightPos0.xyz);
                float3 sunDirObj = mul((float3x3)unity_WorldToObject, sunDirWorld);

                // A cloud is not a single-lobe scatterer. Combining a forward lobe, a
                // gentle back lobe and a sharp near-sun "silver lining" term is the
                // cheap approximation of that, and it is the difference between grey
                // cotton wool and a real backlit storm cloud.
                half cosTheta = dot(rdWorld, sunDirWorld);
                half phase = max(
                    HenyeyGreenstein(cosTheta, _Anisotropy),
                    HenyeyGreenstein(cosTheta, _BackScatter));
                phase += _SilverIntensity * pow(saturate(cosTheta), _SilverExponent);

                half3 sunColor = _LightColor0.rgb * _SunIntensity;

                half3 scattered = 0;
                half transmittance = 1.0;

                [loop]
                for (int s = 0; s < MAX_STEPS; ++s)
                {
                    if (s >= steps || transmittance < 0.01) break;

                    float3 objPos = roObj + rdObj * t;
                    half density = SampleDensity(objPos);

                    if (density > 0.001)
                    {
                        float3 worldPos = roWorld + rdWorld * t;

                        half sunTransmittance = LightTransmittance(objPos, sunDirObj);

                        // Powder term: approximates the multiple scattering that makes
                        // the *outside* of a cloud darker than its lit interior.
                        half powder = 1.0 - exp(-density * 2.0 * _Absorption);
                        powder = lerp(1.0, powder, _PowderStrength);

                        // Cheap multiple-scattering: deep in the cloud, light that has
                        // bounced many times fills the shadowed core with a soft glow
                        // rather than leaving it black. Modelled as a second, much
                        // softer transmittance that does not fall off as fast.
                        half multiScatter = _MultiScatter * pow(sunTransmittance, 0.35);

                        // Height in the volume drives the sky/ground ambient split.
                        half heightFrac = saturate(objPos.y + 0.5);
                        half3 ambient = lerp(_AmbientGround.rgb, _AmbientSky.rgb, heightFrac);

                        half3 luminance = sunColor * (sunTransmittance * phase * powder + multiScatter)
                                            * _ScatterColor.rgb
                                        + ambient
                                        + LightningInScatter(worldPos);

                        // Energy-conserving integration of the segment: analytic rather
                        // than a plain Riemann sum, so step count changes brightness far
                        // less and the perf governor can drop steps without a visible pop.
                        half extinction = density * _Absorption;
                        half segmentTransmittance = exp(-extinction * stepLength);
                        half3 integrated = luminance * density * (1.0 - segmentTransmittance) / max(extinction, 1e-4);

                        scattered += integrated * transmittance;
                        transmittance *= segmentTransmittance;
                    }

                    t += stepLength;
                }

                half alpha = saturate(1.0 - transmittance);
                // Premultiplied output to match the Blend One OneMinusSrcAlpha above.
                return fixed4(scattered, alpha);
            }
            ENDCG
        }
    }

    FallBack Off
}

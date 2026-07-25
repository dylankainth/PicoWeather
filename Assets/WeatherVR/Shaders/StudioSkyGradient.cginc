// The studio-sky gradient math, factored out so it has exactly one home.
// StudioSky.shader evaluates it once per skybox pixel from the view direction;
// Pedestal.shader (and any later glass surface) evaluates it again per glass
// pixel from a reflection direction, optionally folded through KaleidoSplit
// (see WeatherVRGlass.cginc) -- this is what stands in for an environment
// cubemap the project does not have, since the sky is a procedural shader with
// no texture to sample.
#ifndef WEATHERVR_STUDIOSKY_GRADIENT_CGINC
#define WEATHERVR_STUDIOSKY_GRADIENT_CGINC

half3 StudioSkyColour(float3 dir, half3 zenith, half3 horizon, half3 nadir,
                       half sharpness, half3 sunColour, half sunGlow,
                       half sunSharp, float3 sunDir)
{
    half up = pow(saturate(dir.y), 1.0h / sharpness);
    half down = pow(saturate(-dir.y), 1.0h / sharpness);
    half3 col = lerp(lerp(horizon, zenith, up), nadir, down);

    half glow = pow(saturate(dot(dir, sunDir)), sunSharp) * saturate(dir.y + 0.15h);
    col += sunColour * sunGlow * glow;
    return col;
}

#endif // WEATHERVR_STUDIOSKY_GRADIENT_CGINC

package com.weathervr.core

import java.time.Instant
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.sin

/**
 * Fallback weather for when neither the live fetch nor a baked `weather.json` is
 * available.
 *
 * Rather than noise for its own sake this lays out the structure of a real summer
 * mesoscale convective system, because that is what makes the render legible: a squall
 * line oriented NNE–SSW with a narrow convective core, a broad trailing stratiform
 * shield behind it, an anvil of cirrus blown downshear ahead of it, and CAPE peaking
 * just ahead of the line where the atmosphere has not yet been overturned.
 *
 * Datasets produced here are tagged `source = "procedural"` and the app surfaces that
 * in its provenance label — nothing is presented as observed.
 */
object ProceduralWeather {

    /**
     * Axis position of the map centre for the default 20° tilt: with
     * `axis = u*cos(0.35) + v*sin(0.35)` over u,v ∈ [0,1], the centre (u=v=0.5) sits at
     * 0.5*(cos+sin) ≈ 0.6411. Centring the line here puts the convective core and the
     * densest cloud over the middle of the region instead of tucked into one corner.
     */
    const val DEFAULT_PHASE = 0.6411f

    /**
     * Builds a synthetic snapshot on a [gridSize] square grid.
     *
     * @param phase Position of the squall line across the region, in the same units as
     *   `axis` below (roughly 0..1.28 for the default 20° tilt, not a plain 0..1
     *   fraction), letting callers march the system across the map for an animated
     *   forecast timeline.
     * @param now Injected rather than read from the clock so tests are deterministic —
     *   the C# called `DateTime.UtcNow` inline, which makes the output untestable.
     */
    fun generate(
        bounds: GeoBounds,
        gridSize: Int,
        seed: Int,
        phase: Float = DEFAULT_PHASE,
        now: Instant = Instant.now(),
    ): WeatherDataset {
        val size = gridSize.clampTo(4, 256)
        val timestamp = Iso8601.format(now)

        val cells = ArrayList<WeatherCell>(size * size)
        for (y in 0 until size) {
            val v = y / (size - 1).toFloat()
            for (x in 0 until size) {
                val u = x / (size - 1).toFloat()
                cells.add(cellAt(u, v, seed, phase))
            }
        }

        return WeatherDataset(
            source = "procedural",
            attribution = "Synthetic mesoscale convective system — not observed data.",
            observationTimeUtc = timestamp,
            generatedUtc = timestamp,
            minLatitude = bounds.minLatitude,
            maxLatitude = bounds.maxLatitude,
            minLongitude = bounds.minLongitude,
            maxLongitude = bounds.maxLongitude,
            gridWidth = size,
            gridHeight = size,
            layers = WeatherLayerBand.defaults(),
            cells = cells,
        )
    }

    private fun cellAt(u: Float, v: Float, seed: Int, phase: Float): WeatherCell {
        // Squall line: a straight front tilted ~20° from north, advancing east.
        // `s` is signed distance ahead of (+) or behind (−) the line, in map units.
        val tiltRadians = 0.35f
        val axis = u * cos(tiltRadians) + v * sin(tiltRadians)
        // Waviness so the line is not a ruler-straight artefact.
        val waviness = 0.055f * (Noise.fbm2(v * 3.1f, 7.3f, 3, 2f, 0.5f, seed + 5) - 0.5f) * 2f
        val s = axis - (phase + waviness)

        // --- convective core -----------------------------------------------------
        // Band of towering cumulonimbus straddling the line, broken into discrete cells
        // along it as real squall lines are.
        val cellular = 0.55f + 0.45f * Noise.fbm2(axis * 4f, (v - u * 0.3f) * 14f, 3, 2f, 0.55f, seed + 19)
        val core = gaussian(s, 0.075f) * cellular

        // --- trailing stratiform shield --------------------------------------------
        // Broad layered cloud and steady rain behind the line: ramps in just behind the
        // convective core and thins towards the back edge.
        val trailingRise = smoothStep(0f, 1f, inverseLerp(-0.07f, -0.27f, s))
        val trailingFade = smoothStep(0f, 1f, inverseLerp(-0.55f, -1.05f, s))
        val trailing = trailingRise * (1f - trailingFade)

        // --- forward anvil ------------------------------------------------------------
        // Cirrus blown downshear, well ahead of the surface line, no rain under it.
        val anvil = smoothStep(0f, 1f, inverseLerp(0.30f, 0.02f, s)) *
            smoothStep(0f, 1f, inverseLerp(-0.05f, 0.05f, s))

        // Fair-weather cumulus scattered across the undisturbed air mass.
        val fairWeather = maxOf(0f, Noise.fbm2(u * 6.5f, v * 6.5f, 4, 2f, 0.5f, seed + 61) - 0.52f) * 1.6f

        // --- layer assembly --------------------------------------------------------------
        val low = (core * 0.95f + trailing * 0.55f + fairWeather * 0.7f).clamp01()
        val mid = (core * 0.9f + trailing * 0.8f + fairWeather * 0.25f).clamp01()
        val high = (core * 0.8f + trailing * 0.45f + anvil * 0.85f).clamp01()

        // Total cover is the random-overlap combination of the three layers, which is how
        // ECMWF derives it: 1 − Π(1 − c_i).
        val total = 1f - (1f - low) * (1f - mid) * (1f - high)

        // --- precipitation ------------------------------------------------------------------
        // Convective core delivers the heavy rates; the stratiform region gives a long tail
        // of light steady rain. Anvil cirrus precipitates nothing.
        var precip = core * 26f * cellular + trailing * 2.4f
        precip = maxOf(0f, precip - 0.15f) // trim the drizzle floor

        // --- instability -----------------------------------------------------------------------
        // CAPE peaks in the inflow just ahead of the line and is consumed behind it.
        val inflow = gaussian(s - 0.09f, 0.13f)
        val wake = smoothStep(0f, 1f, inverseLerp(-0.05f, -0.35f, s))
        var cape = 320f + 2600f * inflow * (0.7f + 0.3f * Noise.fbm2(u * 5f, v * 5f, 3, 2f, 0.5f, seed + 88))
        cape *= 1f - 0.75f * wake

        // --- surface fields ----------------------------------------------------------------------
        // Cold pool behind the line: several degrees of outflow-driven cooling.
        val temperature = 31.5f - 6.5f * wake - 2.0f * core +
            1.2f * (Noise.fbm2(u * 4f, v * 4f, 3, 2f, 0.5f, seed + 133) - 0.5f) * 2f

        // Prevailing south-westerly flow, with divergent outflow at the line.
        val windU = 6.2f + 9f * core
        val windV = 4.4f - 3f * wake

        return WeatherCell(
            cloudTotal = total,
            cloudLow = low,
            cloudMid = mid,
            cloudHigh = high,
            precipitationMmHr = precip,
            capeJkg = cape,
            lightningPotential = Atmosphere.lightningPotential(cape, precip),
            temperatureC = temperature,
            windU = windU,
            windV = windV,
        )
    }

    /** Unit-height Gaussian of standard deviation [sigma]. */
    private fun gaussian(x: Float, sigma: Float): Float {
        val t = x / sigma
        return exp(-0.5f * t * t)
    }
}

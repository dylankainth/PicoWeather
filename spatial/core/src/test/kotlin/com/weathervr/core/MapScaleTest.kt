package com.weathervr.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The unit-convention guard.
 *
 * This mirrors `tools/verify.py`'s MapScale block, which exists because the Unity
 * project shipped this wrong twice: once when terrain relief was built in metres while
 * everything else was built in map units (terrain came out exactly 2× too tall), and
 * once when the region moved from Shanghai's 50 km span to London's 5 km without
 * retuning the exaggeration constants (a 43.7 m hill rendered as an 84 cm spike).
 *
 * Both failures are invisible in code review and obvious in a headset. The numbers
 * below are the current shipping configuration.
 */
class MapScaleTest {

    private val mapSizeMeters = 2.0f
    private val regionSpanMeters = 5_000.0f
    private val scale = MapScale(
        mapSizeMeters = mapSizeMeters,
        regionSpanMeters = regionSpanMeters,
        verticalExaggeration = 0.4f,
        terrainReliefExaggeration = 1.2f,
        atmosphereFloorMeters = 0f,
    )

    @Test
    fun `horizontal scale is one to twenty five hundred`() {
        assertEquals(1f / 2500f, scale.horizontal, 1e-9f)
    }

    @Test
    fun `a two kilometre cloud sits a sensible distance above the table`() {
        // ~29 cm on a 2 m map: high enough to read as "above the city", low enough to
        // stay in frame. If a change to the exaggeration constants breaks this, the
        // cloud column either vanishes into the terrain or towers out of view.
        val meters = scale.altitudeToMeters(2_000f)
        assertTrue(meters in 0.2f..0.4f, "2 km cloud should sit ~0.29 m up, got $meters m")
    }

    @Test
    fun `londons real relief stays a gentle bump`() {
        // 43.7 m is the peak in the real baked terrain.bin for this region. Rendered, it
        // must be under a centimetre — this is the exact value that regressed to 84 cm.
        val mapUnits = scale.terrainElevationToMapUnits(43.7f)
        val meters = mapUnits * mapSizeMeters
        assertTrue(meters < 0.02f, "43.7 m of relief should render under 2 cm, got $meters m")
        assertTrue(meters > 0.001f, "...but not be flattened away entirely, got $meters m")
    }

    @Test
    fun `sea level and below flattens to zero`() {
        assertEquals(0f, scale.terrainElevationToMapUnits(0f), 1e-9f)
        assertEquals(0f, scale.terrainElevationToMapUnits(-89f), 1e-9f)
    }

    @Test
    fun `buildings keep true proportions against the ground`() {
        // Buildings use `horizontal`, not `vertical`, so a tower is to the map exactly
        // what it is to the city. 310 m on a 5 km/2 m map is 12.4 cm.
        val shardMeters = scale.buildingHeightToMapUnits(310f) * mapSizeMeters
        assertEquals(310f * scale.horizontal, shardMeters, 1e-6f)
        assertEquals(0.124f, shardMeters, 1e-4f)
    }

    @Test
    fun `altitude is compressed relative to building height`() {
        // Worth stating plainly because the field is named "exaggeration" and at its
        // shipping value of 0.4 it *compresses*: vertical = horizontal × 0.4. So 310 m
        // of altitude renders shorter than a 310 m tower. That is deliberate — clouds
        // sit at kilometres and would leave the table at true scale — but it means the
        // name reads backwards, and anyone assuming ×0.4 makes things taller will tune
        // it the wrong way.
        val asBuilding = scale.buildingHeightToMapUnits(310f)
        val asAltitude = scale.altitudeToMapUnits(310f)
        assertTrue(
            asAltitude < asBuilding,
            "expected altitude ($asAltitude) to be compressed below building height ($asBuilding)",
        )
        assertEquals(0.4f, asAltitude / asBuilding, 1e-5f)
    }

    @Test
    fun `map units are not metres`() {
        // The whole trap in one assertion: a value in metres is NOT a local coordinate.
        val oneMetre = 1f
        assertEquals(0.5f, scale.metersToMapUnits(oneMetre), 1e-9f)
        assertTrue(
            scale.metersToMapUnits(oneMetre) != oneMetre,
            "if these are ever equal the map is 1 m across and this test proves nothing",
        )
    }

    @Test
    fun `degenerate inputs are clamped rather than dividing by zero`() {
        val silly = MapScale(0f, 0f, 1f, 1f, 0f)
        assertTrue(silly.horizontal.isFinite())
        assertTrue(silly.metersToMapUnits(1f).isFinite())
    }
}

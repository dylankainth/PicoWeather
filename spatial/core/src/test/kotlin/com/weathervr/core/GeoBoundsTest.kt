package com.weathervr.core

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** Region geometry and the geo ⇄ map-local conversions. */
class GeoBoundsTest {

    // The shipping region: the City of London skyscraper cluster.
    private val centerLat = 51.5136
    private val centerLon = -0.0832
    private val spanKm = 5.0

    private val london = GeoBounds.fromCenterSpan(centerLat, centerLon, spanKm)

    @Test
    fun `region is square on the ground, not in degrees`() {
        // The whole point of fromCenterSpan: at 51.5°N a degree of longitude is only
        // ~62% of a degree of latitude, so equal degree spans would give a rectangle
        // half again as wide as it is tall.
        assertEquals(spanKm * 1000.0, london.widthMeters, 1.0)
        assertEquals(spanKm * 1000.0, london.heightMeters, 1.0)

        assertTrue(
            london.longitudeSpan > london.latitudeSpan * 1.5,
            "longitude span should be widened at this latitude, got " +
                "${london.longitudeSpan} vs ${london.latitudeSpan}",
        )
    }

    @Test
    fun `centre round trips`() {
        assertEquals(centerLat, london.centerLatitude, 1e-9)
        assertEquals(centerLon, london.centerLongitude, 1e-9)
    }

    @Test
    fun `centre maps to the middle of the map`() {
        val local = london.toLocal(centerLat, centerLon)
        assertEquals(0f, local.x, 1e-5f)
        assertEquals(0f, local.z, 1e-5f)
    }

    @Test
    fun `corners map to the corners of the unit square`() {
        val sw = london.toLocal(london.minLatitude, london.minLongitude)
        assertEquals(-0.5f, sw.x, 1e-5f)
        assertEquals(-0.5f, sw.z, 1e-5f)

        val ne = london.toLocal(london.maxLatitude, london.maxLongitude)
        assertEquals(0.5f, ne.x, 1e-5f)
        assertEquals(0.5f, ne.z, 1e-5f)
    }

    @Test
    fun `north is positive Z and east is positive X`() {
        // Get this backwards and the map renders mirrored — which reads as "the data is
        // wrong" rather than "the axes are swapped", so it is worth pinning down.
        val north = london.toLocal(london.maxLatitude, centerLon)
        val east = london.toLocal(centerLat, london.maxLongitude)
        assertTrue(north.z > 0f, "north should be +Z")
        assertTrue(abs(north.x) < 1e-5f, "due north should not move in X")
        assertTrue(east.x > 0f, "east should be +X")
        assertTrue(abs(east.z) < 1e-5f, "due east should not move in Z")
    }

    @Test
    fun `local and geographic round trip`() {
        val points = listOf(
            centerLat to centerLon,
            51.5074 to -0.1278,   // roughly Westminster, inside the region
            london.minLatitude to london.maxLongitude,
        )
        for ((lat, lon) in points) {
            val back = london.fromLocal(london.toLocal(lat, lon))
            assertEquals(lat, back.latitude, 1e-4, "latitude round trip at $lat, $lon")
            assertEquals(lon, back.longitude, 1e-4, "longitude round trip at $lat, $lon")
        }
    }

    @Test
    fun `contains rejects points outside the region`() {
        assertTrue(london.contains(centerLat, centerLon))
        assertFalse(london.contains(centerLat + 1.0, centerLon), "a degree north is 111 km away")
        assertFalse(london.contains(centerLat, centerLon + 1.0))
    }

    @Test
    fun `off-map points are not clamped`() {
        // Callers cull off-map buildings by testing for u,v outside [0,1]; clamping here
        // would silently pile them onto the edge instead.
        val uv = london.toNormalized(london.maxLatitude + 0.1, centerLon)
        assertTrue(uv.v > 1f, "expected v > 1 off the north edge, got ${uv.v}")
    }

    @Test
    fun `the poles do not divide by zero`() {
        val polar = GeoBounds.fromCenterSpan(89.999, 0.0, 5.0)
        assertTrue(polar.longitudeSpan.isFinite(), "longitude span went non-finite at the pole")
    }
}

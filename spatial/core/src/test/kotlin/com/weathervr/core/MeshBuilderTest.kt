package com.weathervr.core

import kotlin.math.abs
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Mesh generation.
 *
 * The properties tested here are the ones that fail *invisibly* in code review and
 * loudly on a headset: winding (an inside-out building looks like a missing roof),
 * normals (a zero normal renders black), and the map-unit convention (relief built in
 * metres instead of map units made the Unity terrain exactly 2× too tall).
 */
class MeshBuilderTest {

    private val bounds = GeoBounds.fromCenterSpan(51.5136, -0.0832, 5.0)
    private val scale = MapScale(
        mapSizeMeters = 2f,
        regionSpanMeters = 5000f,
        verticalExaggeration = 0.4f,
        terrainReliefExaggeration = 1.2f,
        atmosphereFloorMeters = 0f,
    )

    private fun terrain(resolution: Int = 64) = ProceduralTerrain.generate(bounds, resolution, seed = 4)

    // -------------------------------------------------------------------- terrain

    @Test
    fun `terrain mesh has the expected topology`() {
        val mesh = TerrainMeshBuilder.build(terrain(), 32, scale)
        assertEquals(32 * 32, mesh.vertexCount)
        assertEquals(31 * 31 * 2, mesh.triangleCount)
        assertTrue(mesh.indices.all { it in 0 until mesh.vertexCount }, "index out of range")
    }

    @Test
    fun `terrain mesh spans the unit square`() {
        val mesh = TerrainMeshBuilder.build(terrain(), 16, scale)
        var minX = Float.MAX_VALUE
        var maxX = -Float.MAX_VALUE
        var minZ = Float.MAX_VALUE
        var maxZ = -Float.MAX_VALUE
        for (i in 0 until mesh.vertexCount) {
            val p = mesh.getPosition(i)
            minX = minOf(minX, p.x); maxX = maxOf(maxX, p.x)
            minZ = minOf(minZ, p.z); maxZ = maxOf(maxZ, p.z)
        }
        assertEquals(-0.5f, minX, 1e-5f)
        assertEquals(0.5f, maxX, 1e-5f)
        assertEquals(-0.5f, minZ, 1e-5f)
        assertEquals(0.5f, maxZ, 1e-5f)
    }

    @Test
    fun `terrain relief is in map units, not metres`() {
        // The regression that shipped once. A 44 m peak on a 5 km/2 m map must come out
        // around 0.0042 map units — if this reads ~0.008 the conversion was applied
        // twice, and if it reads tens the relief is still in metres.
        val field = terrain(128)
        val mesh = TerrainMeshBuilder.build(field, 64, scale)

        var maxY = -Float.MAX_VALUE
        for (i in 0 until mesh.vertexCount) maxY = maxOf(maxY, mesh.getPosition(i).y)

        val expected = scale.terrainElevationToMapUnits(field.maxElevation)
        assertTrue(maxY <= expected + 1e-4f, "peak $maxY exceeds the field's own maximum $expected")
        assertTrue(maxY < 0.02f, "relief of $maxY map units is far too tall for London")
        assertTrue(maxY > 0f, "terrain is completely flat")
    }

    @Test
    fun `terrain faces upward`() {
        // Every normal on a heightfield should have a positive Y. A flipped winding
        // makes the whole map invisible from above.
        val mesh = TerrainMeshBuilder.build(terrain(), 24, scale)
        for (i in 0 until mesh.vertexCount) {
            assertTrue(mesh.getNormal(i).y > 0.5f, "vertex $i normal points sideways or down: ${mesh.getNormal(i)}")
        }
    }

    @Test
    fun `terrain vertex colours stay in range and mark water`() {
        val field = terrain(128)
        val mesh = TerrainMeshBuilder.build(field, 64, scale)

        var waterVertices = 0
        for (i in 0 until mesh.vertexCount) {
            val r = mesh.colors[i * 4]
            val g = mesh.colors[i * 4 + 1]
            val b = mesh.colors[i * 4 + 2]
            val a = mesh.colors[i * 4 + 3]
            for (c in listOf(r, g, b, a)) assertTrue(c in 0f..1f, "colour channel $c out of range at $i")
            if (r > 0.5f) waterVertices++
        }
        assertTrue(waterVertices > 0, "no water marked anywhere — the Thames is missing")
        assertTrue(waterVertices < mesh.vertexCount, "the entire map is water")
    }

    @Test
    fun `terrain lod chain gets progressively cheaper`() {
        val chain = TerrainMeshBuilder.buildLodChain(terrain(), scale, 64)
        assertEquals(3, chain.size)
        assertTrue(chain[0].triangleCount > chain[1].triangleCount)
        assertTrue(chain[1].triangleCount > chain[2].triangleCount)
    }

    @Test
    fun `terrain mesh stays inside the triangle budget`() {
        // The documented budget is 40k triangles for LOD0.
        val mesh = TerrainMeshBuilder.build(terrain(256), 128, scale)
        assertTrue(mesh.triangleCount <= 40_000, "LOD0 is ${mesh.triangleCount} triangles, over budget")
    }

    // ------------------------------------------------------------------ buildings

    @Test
    fun `buildings extrude with correct counts`() {
        val dataset = ProceduralBuildings.generate(bounds, seed = 5)
        val result = BuildingMeshBuilder.build(dataset, terrain(), bounds, scale, maxBuildings = 1000)

        assertNotNull(result.mesh)
        assertEquals(dataset.buildings.size, result.builtCount)
        assertEquals(0, result.droppedCount)
        // Each rectangle: 4 walls × 4 verts, plus a roof fan of 1 + 4 × 2.
        assertEquals(dataset.buildings.size * (16 + 9), result.mesh!!.vertexCount)
    }

    @Test
    fun `the building cap drops the excess and reports it`() {
        val dataset = ProceduralBuildings.generate(bounds, seed = 5)
        val result = BuildingMeshBuilder.build(dataset, terrain(), bounds, scale, maxBuildings = 3)
        assertEquals(3, result.builtCount)
        assertEquals(dataset.buildings.size - 3, result.droppedCount)
    }

    @Test
    fun `buildings sit on the terrain, not at sea level`() {
        val field = terrain(128)
        val dataset = ProceduralBuildings.generate(bounds, seed = 5)
        val result = BuildingMeshBuilder.build(dataset, field, bounds, scale, maxBuildings = 1000)
        val mesh = result.mesh!!

        var minY = Float.MAX_VALUE
        var maxY = -Float.MAX_VALUE
        for (i in 0 until mesh.vertexCount) {
            val y = mesh.getPosition(i).y
            minY = minOf(minY, y)
            maxY = maxOf(maxY, y)
        }

        assertTrue(minY >= 0f, "a building base sank below the map plane: $minY")

        // The 180 m landmark. Deriving the expectation through MapScale rather than
        // writing a number down: the arithmetic is 180 × horizontal ÷ mapSizeMeters =
        // 180 × (2/5000) ÷ 2 = 0.036 map units, and doing it by hand is how you end up
        // asserting a figure that is really in metres — which is the mistake this whole
        // unit convention exists to prevent, and which this assertion originally made.
        val landmarkTop = scale.buildingHeightToMapUnits(180f)
        assertTrue(
            maxY > landmarkTop * 0.95f,
            "tallest building reached $maxY map units, expected about $landmarkTop",
        )
        assertTrue(
            maxY < landmarkTop * 1.2f,
            "buildings are being exaggerated: $maxY map units against an expected $landmarkTop",
        )
    }

    @Test
    fun `roofs face upward and walls face outward`() {
        // The winding property. A footprint wound counter-clockwise from above must
        // produce roof normals with +Y, and wall normals pointing away from the centre.
        val dataset = ProceduralBuildings.generate(bounds, seed = 7)
        val single = dataset.copy(buildings = listOf(dataset.buildings.first()))
        val mesh = BuildingMeshBuilder.build(single, null, bounds, scale, maxBuildings = 1).mesh!!

        // Footprint centre in map space.
        val ring = single.buildings.first().points.map { bounds.toLocal(it.latitude, it.longitude) }
        val cx = ring.map { it.x }.average().toFloat()
        val cz = ring.map { it.z }.average().toFloat()

        var upward = 0
        var outward = 0
        var inward = 0
        for (i in 0 until mesh.vertexCount) {
            val n = mesh.getNormal(i)
            val p = mesh.getPosition(i)
            if (n.y > 0.9f) {
                upward++
            } else if (abs(n.y) < 0.5f) {
                // A wall. Does its normal point away from the footprint centre?
                val dot = n.x * (p.x - cx) + n.z * (p.z - cz)
                if (dot > 0f) outward++ else inward++
            }
        }

        assertTrue(upward > 0, "no upward-facing roof vertices — the roof fan is inverted")
        assertTrue(outward > 0, "no outward-facing walls found")
        assertEquals(0, inward, "$inward wall vertices face into the building — winding is reversed")
    }

    @Test
    fun `an empty dataset produces no mesh rather than an empty one`() {
        val empty = BuildingDataset(
            minLatitude = bounds.minLatitude, maxLatitude = bounds.maxLatitude,
            minLongitude = bounds.minLongitude, maxLongitude = bounds.maxLongitude,
        )
        val result = BuildingMeshBuilder.build(empty, null, bounds, scale, maxBuildings = 100)
        assertNull(result.mesh, "an empty dataset should yield no mesh at all")
        assertEquals(0, result.builtCount)
    }

    @Test
    fun `degenerate footprints are skipped`() {
        val twoPoints = BuildingDataset(
            minLatitude = bounds.minLatitude, maxLatitude = bounds.maxLatitude,
            minLongitude = bounds.minLongitude, maxLongitude = bounds.maxLongitude,
            buildings = listOf(
                BuildingRecord(20f, doubleArrayOf(51.51, -0.08, 51.52, -0.07)),
            ),
        )
        val result = BuildingMeshBuilder.build(twoPoints, null, bounds, scale, maxBuildings = 100)
        assertEquals(0, result.builtCount, "a two-point 'polygon' is not a building")
    }

    @Test
    fun `real osm buildings extrude`() {
        val file = listOf(
            java.io.File("../../Assets/StreamingAssets/WeatherData/buildings.json"),
            java.io.File("../Assets/StreamingAssets/WeatherData/buildings.json"),
        ).firstOrNull { it.isFile }
        if (file == null) {
            println("SKIP: no baked buildings.json")
            return
        }

        val dataset = WeatherJson.decodeBuildings(file.readText())
        val result = BuildingMeshBuilder.build(dataset, terrain(128), bounds, scale, maxBuildings = 600)

        val mesh = result.mesh
        assertNotNull(mesh)
        assertTrue(result.builtCount > 100, "only ${result.builtCount} real buildings extruded")
        // Every normal must be finite and non-zero, or the facades render black.
        for (i in 0 until mesh.vertexCount) {
            val n = mesh.getNormal(i)
            val length = n.x * n.x + n.y * n.y + n.z * n.z
            assertTrue(length > 0.9f && length.isFinite(), "vertex $i has a degenerate normal")
        }
        println("extruded ${result.builtCount} real buildings -> $mesh")
    }

    // ------------------------------------------------------------------ mesh data

    @Test
    fun `normals are area weighted and unit length`() {
        val mesh = MeshData(3, 1)
        mesh.setPosition(0, 0f, 0f, 0f)
        mesh.setPosition(1, 0f, 0f, 1f)
        mesh.setPosition(2, 1f, 0f, 0f)
        mesh.setTriangle(0, 0, 1, 2)
        mesh.recalculateNormals()

        // cross(v1-v0, v2-v0) = cross((0,0,1),(1,0,0)) = (0*0-1*0, 1*1-0*0, 0) = (0,1,0)
        val n = mesh.getNormal(0)
        assertEquals(0f, n.x, 1e-6f)
        assertEquals(1f, n.y, 1e-6f)
        assertEquals(0f, n.z, 1e-6f)
    }

    @Test
    fun `a degenerate triangle does not produce a black normal`() {
        val mesh = MeshData(3, 1)
        mesh.setPosition(0, 0f, 0f, 0f)
        mesh.setPosition(1, 0f, 0f, 0f)
        mesh.setPosition(2, 0f, 0f, 0f)
        mesh.setTriangle(0, 0, 1, 2)
        mesh.recalculateNormals()
        assertEquals(1f, mesh.getNormal(0).y, 1e-6f, "a zero normal renders black under any lighting")
    }

    @Test
    fun `the 16 bit index limit is reported`() {
        assertTrue(!MeshData(1000, 1).needs32BitIndices)
        assertTrue(MeshData(70_000, 1).needs32BitIndices, "a silent 16-bit overflow renders as garbage")
    }
}
